using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="ITrayService"/> over <c>Shell_NotifyIconW</c> (<c>NOTIFYICON_VERSION_4</c>) owned by the adapter's hidden
/// shell window. Icons are re-added when Explorer restarts (<c>TaskbarCreated</c>); icons and HICONs are destroyed on
/// disposal.
/// </summary>
internal sealed partial class WindowsTrayService : ITrayService, IDisposable, IAsyncDisposable
{
    private readonly MessageWindowThread _thread;
    private readonly WindowsNativeMenuService _menus;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<WindowsTrayIcon> _icons = [];
    private IDisposable? _handler;
    private uint _taskbarCreatedMessage;
    private uint _lastIconId;
    private int _disposed;

    public WindowsTrayService(MessageWindowThread thread, WindowsNativeMenuService menus, ILogger<WindowsTrayService> logger)
    {
        _thread = thread;
        _menus = menus;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Tray;

    public IReadOnlyList<ITrayIcon> Icons
    {
        get
        {
            lock (_gate)
            {
                return [.. _icons];
            }
        }
    }

    internal MessageWindowThread Thread => _thread;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public async ValueTask<NativeResult<ITrayIcon>> CreateAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var icon = IconLoader.Load(options.IconPath, options.IconData, Capability);
        if (!icon.IsSuccess)
        {
            return icon.Error;
        }

        WindowsTrayIcon? trayIcon = null;
        try
        {
            EnsureHandler();
            var id = options.Id ?? Guid.NewGuid().ToString("N");
            trayIcon = new WindowsTrayIcon(this, _thread.ShellWindow, id, Interlocked.Increment(ref _lastIconId), options.Tooltip, icon.Value, options.Menu);
            var added = await _thread.InvokeAsync(trayIcon.AddToShell).ConfigureAwait(false);
            if (!added)
            {
                trayIcon.ReleaseResources();
                return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "Shell_NotifyIcon could not add the icon (is Explorer running?).");
            }

            lock (_gate)
            {
                _icons.Add(trayIcon);
            }

            return trayIcon;
        }
        catch (Exception ex)
        {
            if (trayIcon is not null)
            {
                trayIcon.ReleaseResources();
            }
            else
            {
                icon.Value.Dispose();
            }

            return NativeError.FromException(Capability, ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        WindowsTrayIcon[] icons;
        lock (_gate)
        {
            icons = [.. _icons];
            _icons.Clear();
        }

        foreach (var icon in icons)
        {
            icon.Dispose();
        }

        _handler?.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Remove(WindowsTrayIcon icon)
    {
        lock (_gate)
        {
            _ = _icons.Remove(icon);
        }
    }

    internal void RaiseSafely(EventHandler? handler, object sender)
    {
        if (handler is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                handler(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogEventHandlerFailed(_logger, ex);
            }
        });
    }

    private void EnsureHandler()
    {
        if (Volatile.Read(ref _handler) is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_handler is null)
            {
                const uint MSGFLT_ALLOW = 1;
                _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

                // Elevated processes only receive the broadcast from the (non-elevated) shell when UIPI lets it through.
                _ = NativeMethods.ChangeWindowMessageFilterEx(_thread.ShellWindow, _taskbarCreatedMessage, MSGFLT_ALLOW, 0);
                _handler = _thread.AddHandler(OnMessage);
            }
        }
    }

    private bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: every icon must be added again. Let other handlers observe the broadcast too.
            foreach (var icon in Icons.Cast<WindowsTrayIcon>())
            {
                if (!icon.AddToShell())
                {
                    LogReAddFailed(_logger, icon.Id);
                }
            }

            return false;
        }

        if (message != Win32.WM_BNS_TRAY || hwnd != _thread.ShellWindowHandle)
        {
            return false;
        }

        // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, HIWORD(lParam) = icon id, wParam = anchor point.
        var notification = (uint)Win32.LoWord(lParam);
        var iconId = (uint)Win32.HiWord(lParam);
        WindowsTrayIcon? target;
        lock (_gate)
        {
            target = _icons.Find(i => i.NativeId == iconId);
        }

        if (target is null)
        {
            return true;
        }

        switch (notification)
        {
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                target.OnSelected();
                break;
            case Win32.WM_LBUTTONDBLCLK:
                target.OnDoubleClicked();
                break;
            case Win32.WM_CONTEXTMENU:
                if (target.Menu is { } menu)
                {
                    var anchor = new POINT { X = Win32.SignedLoWord(wParam), Y = Win32.SignedHiWord(wParam) };
                    _menus.ShowTrayMenu(menu, hwnd, anchor);
                }

                break;
        }

        return true;
    }

    [LoggerMessage(EventId = 3301, Level = LogLevel.Error, Message = "A notification-area icon event handler failed.")]
    private static partial void LogEventHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3302, Level = LogLevel.Warning, Message = "Notification-area icon {IconId} could not be re-added after Explorer restarted.")]
    private static partial void LogReAddFailed(ILogger logger, string iconId);
}

/// <summary>A notification-area icon.</summary>
internal sealed class WindowsTrayIcon : ITrayIcon, IDisposable
{
    private const int MaxTooltipLength = 127;
    private readonly WindowsTrayService _owner;
    private readonly nint _shellWindow;
    private readonly Lock _gate = new();
    private SafeIconHandle _icon;
    private string _tooltip;
    private volatile NativeMenu? _menu;
    private long _lastDoubleClick = long.MinValue / 2; // Only touched on the message thread.
    private int _disposed;

    public WindowsTrayIcon(WindowsTrayService owner, nint shellWindow, string id, uint nativeId, string tooltip, SafeIconHandle icon, NativeMenu? menu)
    {
        _owner = owner;
        _shellWindow = shellWindow;
        Id = id;
        NativeId = nativeId;
        _tooltip = Truncate(tooltip);
        _icon = icon;
        _menu = menu;
    }

    public string Id { get; }

    public uint NativeId { get; }

    public NativeMenu? Menu => _menu;

    public event EventHandler? Clicked;

    public event EventHandler? DoubleClicked;

    public async ValueTask<NativeResult> SetTooltipAsync(string tooltip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            _tooltip = Truncate(tooltip);
        }

        return await ModifyAsync(Win32.NIF_TIP | Win32.NIF_SHOWTIP).ConfigureAwait(false);
    }

    public async ValueTask<NativeResult> SetIconAsync(string? iconPath, ReadOnlyMemory<byte>? iconData = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var loaded = IconLoader.Load(iconPath, iconData, NativeCapabilities.Tray);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        SafeIconHandle previous;
        lock (_gate)
        {
            previous = _icon;
            _icon = loaded.Value;
        }

        var result = await ModifyAsync(Win32.NIF_ICON).ConfigureAwait(false);
        previous.Dispose();
        return result;
    }

    public ValueTask<NativeResult> SetMenuAsync(NativeMenu? menu, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _menu = menu;
        return ValueTask.FromResult(NativeResult.Ok);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.Remove(this);

        // NIM_DELETE identifies the icon by window and id, so it can run on any thread, even after the loop stopped.
        lock (_gate)
        {
            var data = CreateData(0);
            _ = NativeMethods.Shell_NotifyIconW(Win32.NIM_DELETE, &data);
            _icon.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Adds the icon to the notification area and opts into version 4 behavior. Runs on the message thread.</summary>
    internal unsafe bool AddToShell()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var data = CreateData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
            if (!NativeMethods.Shell_NotifyIconW(Win32.NIM_ADD, &data))
            {
                // After an Explorer restart the icon may survive; modify it instead.
                if (!NativeMethods.Shell_NotifyIconW(Win32.NIM_MODIFY, &data))
                {
                    return false;
                }
            }

            data.uVersion = Win32.NOTIFYICON_VERSION_4;
            return NativeMethods.Shell_NotifyIconW(Win32.NIM_SETVERSION, &data);
        }
    }

    /// <summary>Destroys the HICON without touching the shell (creation failed).</summary>
    internal void ReleaseResources()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        _icon.Dispose();
    }

    internal void OnSelected()
    {
        // The button release that completes a double click also produces NIN_SELECT; ignore it, but only within the
        // double-click interval so a later genuine click is never swallowed.
        if (Environment.TickCount64 - _lastDoubleClick <= NativeMethods.GetDoubleClickTime())
        {
            _lastDoubleClick = long.MinValue / 2;
            return;
        }

        _owner.RaiseSafely(Clicked, this);
    }

    internal void OnDoubleClicked()
    {
        _lastDoubleClick = Environment.TickCount64;
        _owner.RaiseSafely(DoubleClicked, this);
    }

    private async ValueTask<NativeResult> ModifyAsync(uint flags)
    {
        try
        {
            var modified = await _owner.Thread.InvokeAsync(() => Modify(flags)).ConfigureAwait(false);
            return modified
                ? NativeResult.Ok
                : NativeError.Failed(NativeCapabilities.Tray, NativeErrorCodes.PlatformError, "Shell_NotifyIcon could not update the icon.");
        }
        catch (Exception ex)
        {
            return NativeError.FromException(NativeCapabilities.Tray, ex);
        }
    }

    private unsafe bool Modify(uint flags)
    {
        lock (_gate)
        {
            var data = CreateData(flags);
            return Volatile.Read(ref _disposed) == 0 && NativeMethods.Shell_NotifyIconW(Win32.NIM_MODIFY, &data);
        }
    }

    // Must be called under _gate.
    private unsafe NOTIFYICONDATAW CreateData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _shellWindow,
            uID = NativeId,
            uFlags = flags,
            uCallbackMessage = Win32.WM_BNS_TRAY,
            hIcon = _icon.IsClosed ? 0 : _icon.DangerousGetHandle(),
        };

        var tip = _tooltip.AsSpan();
        var destination = new Span<char>(data.szTip, 128);
        tip.CopyTo(destination);
        destination[tip.Length] = '\0';
        return data;
    }

    private static string Truncate(string value) => value.Length <= MaxTooltipLength ? value : value[..MaxTooltipLength];
}
