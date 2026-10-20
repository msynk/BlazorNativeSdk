using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="IGlobalShortcutService"/> over <c>RegisterHotKey</c> on the adapter's message-only window. Hot keys are
/// registered with <c>MOD_NOREPEAT</c>; callbacks run on the thread pool.
/// </summary>
internal sealed partial class WindowsGlobalShortcutService : IGlobalShortcutService, IDisposable, IAsyncDisposable
{
    private const int MaxHotKeyId = 0xBFFF;

    private readonly MessageWindowThread _thread;
    private readonly bool _ownsThread;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Registration> _registrations = [];
    private IDisposable? _handler;
    private int _lastId;
    private int _disposed;

    public WindowsGlobalShortcutService(MessageWindowThread thread, ILogger<WindowsGlobalShortcutService> logger, bool ownsThread = false)
    {
        _thread = thread;
        _logger = logger;
        _ownsThread = ownsThread;
    }

    public CapabilityId Capability => NativeCapabilities.GlobalShortcuts;

    public IReadOnlyList<KeyGesture> RegisteredGestures
    {
        get
        {
            lock (_gate)
            {
                return [.. _registrations.Values.Select(static r => r.Gesture)];
            }
        }
    }

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public async ValueTask<NativeResult<IGlobalShortcutRegistration>> RegisterAsync(KeyGesture gesture, Func<ValueTask> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var resolved = gesture.Resolve(NativePlatform.Windows);
        if (!VirtualKeyMap.TryGetVirtualKey(resolved.Key, out var virtualKey))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, $"Key '{resolved.Key}' cannot be used in a global shortcut.");
        }

        var modifiers = VirtualKeyMap.ToHotKeyModifiers(resolved.Modifiers) | Win32.MOD_NOREPEAT;
        try
        {
            EnsureHandler();
            var outcome = await _thread.InvokeAsync(() =>
            {
                var id = NextId();
                if (!NativeMethods.RegisterHotKey(_thread.MessageWindow, id, modifiers, virtualKey))
                {
                    return (Registration: (Registration?)null, Error: Marshal.GetLastPInvokeError());
                }

                // Added on the message thread before any WM_HOTKEY for this id can be dispatched.
                var registration = new Registration(this, id, resolved, callback);
                lock (_gate)
                {
                    _registrations[id] = registration;
                }

                return (Registration: registration, Error: 0);
            }).ConfigureAwait(false);

            if (outcome.Registration is { } created)
            {
                LogRegistered(_logger, resolved);
                return created;
            }

            if (outcome.Error == Win32.ERROR_HOTKEY_ALREADY_REGISTERED)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.Conflict, $"'{resolved}' is already registered as a global hot key (by this or another application).");
            }

            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"RegisterHotKey failed for '{resolved}' with Win32 error {outcome.Error}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        int[] ids;
        lock (_gate)
        {
            ids = [.. _registrations.Keys];
            _registrations.Clear();
        }

        if (ids.Length > 0 && _thread.IsRunning)
        {
            try
            {
                _thread.Invoke(() =>
                {
                    foreach (var id in ids)
                    {
                        _ = NativeMethods.UnregisterHotKey(_thread.MessageWindowHandle, id);
                    }

                    return true;
                });
            }
            catch (Exception ex)
            {
                LogUnregisterFailed(_logger, ex);
            }
        }

        _handler?.Dispose();
        if (_ownsThread)
        {
            _thread.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureHandler()
    {
        if (Volatile.Read(ref _handler) is not null)
        {
            return;
        }

        lock (_gate)
        {
            _handler ??= _thread.AddHandler(OnMessage);
        }
    }

    private int NextId()
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < MaxHotKeyId; attempt++)
            {
                _lastId = _lastId >= MaxHotKeyId ? 1 : _lastId + 1;
                if (!_registrations.ContainsKey(_lastId))
                {
                    return _lastId;
                }
            }
        }

        throw new InvalidOperationException("All global hot key identifiers are in use.");
    }

    private bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != Win32.WM_HOTKEY || hwnd != _thread.MessageWindowHandle)
        {
            return false;
        }

        Registration? registration;
        lock (_gate)
        {
            _ = _registrations.TryGetValue((int)wParam, out registration);
        }

        if (registration is null)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await registration.Callback().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCallbackFailed(_logger, registration.Gesture, ex);
            }
        });
        return true;
    }

    private async ValueTask UnregisterAsync(Registration registration)
    {
        lock (_gate)
        {
            if (!_registrations.Remove(registration.Id))
            {
                return;
            }
        }

        if (!_thread.IsRunning)
        {
            return;
        }

        try
        {
            await _thread.InvokeAsync(() => NativeMethods.UnregisterHotKey(_thread.MessageWindowHandle, registration.Id)).ConfigureAwait(false);
            LogUnregistered(_logger, registration.Gesture);
        }
        catch (Exception ex)
        {
            LogUnregisterFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Global shortcut {Gesture} registered.")]
    private static partial void LogRegistered(ILogger logger, KeyGesture gesture);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Debug, Message = "Global shortcut {Gesture} unregistered.")]
    private static partial void LogUnregistered(ILogger logger, KeyGesture gesture);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Error, Message = "The callback of global shortcut {Gesture} failed.")]
    private static partial void LogCallbackFailed(ILogger logger, KeyGesture gesture, Exception exception);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Warning, Message = "Unregistering global shortcuts failed.")]
    private static partial void LogUnregisterFailed(ILogger logger, Exception exception);

    private sealed class Registration(WindowsGlobalShortcutService owner, int id, KeyGesture gesture, Func<ValueTask> callback) : IGlobalShortcutRegistration
    {
        private int _disposed;

        public int Id { get; } = id;

        public KeyGesture Gesture { get; } = gesture;

        public Func<ValueTask> Callback { get; } = callback;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.UnregisterAsync(this) : ValueTask.CompletedTask;
    }
}
