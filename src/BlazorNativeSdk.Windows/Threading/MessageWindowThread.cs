using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// A dedicated STA thread with a Win32 message loop that owns two hidden windows: a message-only window
/// (<c>HWND_MESSAGE</c>) for hot keys, clipboard notifications and cross-thread marshalling, and a never-shown top-level
/// tool window for notification-area icons and popup menus (message-only windows neither receive the
/// <c>TaskbarCreated</c> broadcast nor can become foreground, which tray menus require).
/// </summary>
/// <remarks>
/// The thread starts lazily on first use. Work is marshalled with a posted private message plus a queue, so callers never
/// block the loop. The window procedure is a static <see cref="UnmanagedCallersOnlyAttribute"/> function, so there is no
/// delegate to keep alive; the owning instance is reached through a <see cref="GCHandle"/> stored in <c>GWLP_USERDATA</c>.
/// </remarks>
internal sealed unsafe partial class MessageWindowThread : IDisposable, IAsyncDisposable
{
    /// <summary>Handles a window message. Return <see langword="true"/> and set <paramref name="result"/> to stop processing.</summary>
    internal delegate bool MessageHandler(nint hwnd, uint message, nint wParam, nint lParam, out nint result);

    private const string ClassName = "BlazorNativeSdk.Windows.MessageWindow";
    private static readonly Lock s_classGate = new();
    private static bool s_classRegistered;

    private readonly ILogger _logger;
    private readonly string _threadName;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MessageHandler[] _handlers = [];
    private Thread? _thread;
    private GCHandle _self;
    private nint _messageWindow;
    private nint _shellWindow;
    private uint _nativeThreadId;
    private int _disposed;
    private volatile bool _loopExited;

    public MessageWindowThread(ILogger<MessageWindowThread>? logger = null, string threadName = "BlazorNativeSdk.Windows message loop")
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _threadName = threadName;
    }

    /// <summary>The message-only window (hot keys, clipboard listener, invoke queue).</summary>
    public nint MessageWindow
    {
        get
        {
            EnsureStarted();
            return _messageWindow;
        }
    }

    /// <summary>The hidden top-level window (notification icons, popup menus, shell broadcasts).</summary>
    public nint ShellWindow
    {
        get
        {
            EnsureStarted();
            return _shellWindow;
        }
    }

    /// <summary>The message-only window without starting the thread (<c>0</c> when not running). For message handlers.</summary>
    public nint MessageWindowHandle => _messageWindow;

    /// <summary>The shell window without starting the thread (<c>0</c> when not running). For message handlers.</summary>
    public nint ShellWindowHandle => _shellWindow;

    /// <summary>The Win32 thread id of the message loop thread.</summary>
    public uint NativeThreadId
    {
        get
        {
            EnsureStarted();
            return _nativeThreadId;
        }
    }

    /// <summary><see langword="true"/> when the caller runs on the message loop thread.</summary>
    public bool IsCurrentThread => _thread is not null && ReferenceEquals(Thread.CurrentThread, _thread);

    /// <summary><see langword="true"/> while the loop is running (started and not disposed).</summary>
    public bool IsRunning => _started.Task.IsCompletedSuccessfully && !_loopExited && Volatile.Read(ref _disposed) == 0;

    /// <summary>Starts the thread and creates the windows if needed. Blocks until they exist.</summary>
    /// <exception cref="ObjectDisposedException">The thread was disposed.</exception>
    /// <exception cref="Win32Exception">The windows could not be created.</exception>
    public void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_started.Task.IsCompleted)
        {
            lock (_gate)
            {
                if (_thread is null)
                {
                    var thread = new Thread(ThreadMain) { IsBackground = true, Name = _threadName };
                    thread.SetApartmentState(ApartmentState.STA);
                    _thread = thread;
                    thread.Start();
                }
            }
        }

        if (!IsCurrentThread)
        {
            _started.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the message thread (inline when already on it).</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    /// <summary>Runs <paramref name="func"/> on the message thread (inline when already on it) and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsCurrentThread)
        {
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        try
        {
            EnsureStarted();
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }

        var item = new WorkItem<T>(func);
        _queue.Enqueue(item);

        // Never post to 0: PostMessage(NULL) would queue the message on the caller's own thread.
        var window = _messageWindow;
        if (_loopExited || window == 0 || !NativeMethods.PostMessageW(window, Win32.WM_BNS_INVOKE, 0, 0))
        {
            // The loop is gone (disposed) or the queue is full: fail whatever is still pending.
            DrainQueue(execute: false);
        }

        return item.Task;
    }

    /// <summary>Runs <paramref name="func"/> on the message thread and waits for the result.</summary>
    public T Invoke<T>(Func<T> func) => IsCurrentThread ? func() : InvokeAsync(func).GetAwaiter().GetResult();

    /// <summary>Registers a handler that observes the messages of both windows. Dispose the result to remove it.</summary>
    public IDisposable AddHandler(MessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }

        return new HandlerRegistration(this, handler);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
        }

        if (thread is not null)
        {
            if (!IsCurrentThread)
            {
                // Wait for startup to finish so the shutdown request reaches a live window.
                _ = _started.Task.Wait(TimeSpan.FromSeconds(5));
            }

            if (_started.Task.IsCompletedSuccessfully)
            {
                _queue.Enqueue(new WorkItem<object?>(() =>
                {
                    Shutdown();
                    return null;
                }));
                if (IsCurrentThread)
                {
                    DrainQueue(execute: true);
                }
                else if (NativeMethods.PostMessageW(_messageWindow, Win32.WM_BNS_INVOKE, 0, 0))
                {
                    if (!thread.Join(TimeSpan.FromSeconds(5)))
                    {
                        LogShutdownTimeout(_logger);
                    }
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThreadMain()
    {
        try
        {
            RegisterWindowClass();
            _self = GCHandle.Alloc(this);
            var instance = NativeMethods.GetModuleHandleW(null);
            var self = GCHandle.ToIntPtr(_self);
            _nativeThreadId = NativeMethods.GetCurrentThreadId();
            _messageWindow = NativeMethods.CreateWindowExW(0, ClassName, "BlazorNativeSdk message window", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, 0, instance, self);
            if (_messageWindow == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the message-only window.");
            }

            _shellWindow = NativeMethods.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, ClassName, "BlazorNativeSdk shell window", Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, self);
            if (_shellWindow == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the hidden shell window.");
            }
        }
        catch (Exception ex)
        {
            LogStartFailed(_logger, ex);
            DestroyWindows();
            _loopExited = true;
            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _started.TrySetException(ex);
            return;
        }

        _started.TrySetResult();
        RunLoop();

        _loopExited = true;
        DestroyWindows();
        DrainQueue(execute: false);
        _self.Free();
    }

    private static void RunLoop()
    {
        MSG msg;
        while (true)
        {
            var result = NativeMethods.GetMessageW(&msg, 0, 0, 0);
            if (result is 0 or -1)
            {
                break;
            }

            _ = NativeMethods.TranslateMessage(&msg);
            _ = NativeMethods.DispatchMessageW(&msg);
        }
    }

    private void Shutdown()
    {
        DestroyWindows();
        NativeMethods.PostQuitMessage(0);
    }

    private void DestroyWindows()
    {
        if (_shellWindow != 0)
        {
            _ = NativeMethods.DestroyWindow(_shellWindow);
            _shellWindow = 0;
        }

        if (_messageWindow != 0)
        {
            _ = NativeMethods.DestroyWindow(_messageWindow);
            _messageWindow = 0;
        }
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Win32.WM_BNS_INVOKE)
        {
            DrainQueue(execute: true);
            return 0;
        }

        if (message == Win32.WM_CLOSE && Volatile.Read(ref _disposed) == 0)
        {
            // Never let a stray WM_CLOSE (task managers, broadcasts) destroy the shared windows.
            return 0;
        }

        var handlers = Volatile.Read(ref _handlers);
        foreach (var handler in handlers)
        {
            try
            {
                if (handler(hwnd, message, wParam, lParam, out var result))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, message, ex);
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void DrainQueue(bool execute)
    {
        while (_queue.TryDequeue(out var item))
        {
            if (execute)
            {
                item.Execute();
            }
            else
            {
                item.Abort(new ObjectDisposedException(nameof(MessageWindowThread), "The Windows message loop has stopped."));
            }
        }
    }

    private void RemoveHandler(MessageHandler handler)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_handlers, handler);
            if (index >= 0)
            {
                _handlers = [.. _handlers.AsSpan(0, index), .. _handlers.AsSpan(index + 1)];
            }
        }
    }

    private static void RegisterWindowClass()
    {
        lock (s_classGate)
        {
            if (s_classRegistered)
            {
                return;
            }

            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = NativeMethods.GetModuleHandleW(null),
                    lpszClassName = className,
                };

                if (NativeMethods.RegisterClassExW(&wc) == 0)
                {
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ERROR_CLASS_ALREADY_EXISTS)
                    {
                        throw new Win32Exception(error, "Could not register the message window class.");
                    }
                }
            }

            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == Win32.WM_NCCREATE)
            {
                var create = (CREATESTRUCTW*)lParam;
                _ = NativeMethods.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, create->lpCreateParams);
            }

            var state = NativeMethods.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
            if (message == Win32.WM_NCDESTROY)
            {
                _ = NativeMethods.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, 0);
            }
            else if (state != 0 && GCHandle.FromIntPtr(state).Target is MessageWindowThread owner)
            {
                return owner.OnMessage(hwnd, message, wParam, lParam);
            }
        }
        catch
        {
            // Exceptions must never cross the native boundary. Handlers already log their own failures.
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "The Windows message loop could not start.")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A handler for window message 0x{Message:X4} failed.")]
    private static partial void LogHandlerFailed(ILogger logger, uint message, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "The Windows message loop did not stop within 5 seconds.")]
    private static partial void LogShutdownTimeout(ILogger logger);

    private abstract class WorkItem
    {
        public abstract void Execute();

        public abstract void Abort(Exception exception);
    }

    private sealed class WorkItem<T>(Func<T> func) : WorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            try
            {
                _completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public override void Abort(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class HandlerRegistration(MessageWindowThread owner, MessageHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.RemoveHandler(handler);
            }
        }
    }
}
