using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// <see cref="INativeMenuService"/> over Win32 menus. Context menus use <c>TrackPopupMenuEx</c> with <c>TPM_RETURNCMD</c>;
/// window menu bars use <c>SetMenu</c> plus a comctl32 window subclass that receives <c>WM_COMMAND</c>.
/// </summary>
/// <remarks>
/// Menu bars live in the standard non-client area: WPF windows with <c>WindowChrome</c>, and WinUI/.NET MAUI windows that
/// extend content into the title bar, draw over it and hide the menu. Accelerators are displayed, not registered:
/// keyboard input goes to the WebView, so handle shortcuts in the page or with <see cref="IGlobalShortcutService"/>.
/// Standard roles: <see cref="NativeMenuRole.Quit"/> posts <c>WM_CLOSE</c> to the main window; edit roles send
/// <c>WM_COPY</c>/<c>WM_CUT</c>/<c>WM_PASTE</c>/<c>WM_UNDO</c>/<c>EM_SETSEL</c>/<c>EM_REDO</c> to the focused control of the
/// window's thread (native edit controls; WebView content generally ignores them, so handle edit roles in the page via
/// <see cref="ItemInvoked"/> when needed).
/// </remarks>
internal sealed partial class WindowsNativeMenuService : INativeMenuService, IDisposable, IAsyncDisposable
{
    private const nuint SubclassId = 0x424E534D; // "BNSM"

    // The subclass procedure finds its service through this table instead of a GCHandle so that a disposed service can
    // never be dereferenced by a window that still carries the subclass.
    private static readonly ConcurrentDictionary<nuint, WindowsNativeMenuService> s_instances = new();
    private static long s_lastInstanceKey;

    private readonly nuint _instanceKey;
    private readonly MessageWindowThread _thread;
    private readonly IWindowsHostBridge _bridge;
    private readonly IServiceProvider? _services;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, MenuBar> _menuBars = [];
    private int _disposed;

    public WindowsNativeMenuService(MessageWindowThread thread, IWindowsHostBridge bridge, ILogger<WindowsNativeMenuService> logger, IServiceProvider? services = null)
    {
        _thread = thread;
        _bridge = bridge;
        _logger = logger;
        _services = services;
        _instanceKey = (nuint)Interlocked.Increment(ref s_lastInstanceKey);
        s_instances[_instanceKey] = this;
    }

    public event EventHandler<MenuItemInvokedEventArgs>? ItemInvoked;

    public CapabilityId Capability => NativeCapabilities.Menus;

    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, WindowsAdapter.Name));

    public async ValueTask<NativeResult> SetApplicationMenuAsync(NativeMenu? menu, string? windowId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var window = ResolveWindow(windowId);
        if (!window.IsSuccess)
        {
            return window.Error;
        }

        MenuPlan? plan = null;
        if (menu is not null)
        {
            try
            {
                plan = Win32MenuPlanner.Plan(menu, Win32MenuPlanner.MenuBarFirstId);
            }
            catch (ArgumentException ex)
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, ex.Message, ex);
            }
        }

        try
        {
            return await _bridge.RunOnUIThreadAsync(() => AttachMenuBar(window.Value, plan, windowId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    public async ValueTask<NativeResult<string>> ShowContextMenuAsync(NativeMenu menu, double? screenX = null, double? screenY = null, string? windowId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        MenuPlan plan;
        try
        {
            plan = Win32MenuPlanner.Plan(menu);
        }
        catch (ArgumentException ex)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, ex.Message, ex);
        }

        if (plan.Entries.Count == 0)
        {
            return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "The menu has no items.");
        }

        POINT? physicalPoint = screenX is { } x && screenY is { } y ? Monitors.DipPointToPhysical(x, y) : null;
        var window = ResolveWindow(windowId);
        if (windowId is not null && !window.IsSuccess)
        {
            return window.Error;
        }

        var owner = window.IsSuccess ? window.Value : 0;
        try
        {
            uint command = 0;
            var shown = false;
            if (owner != 0)
            {
                // Prefer the app window as owner (it stays active while the menu is open); this requires its UI thread.
                (shown, command) = await _bridge.RunOnUIThreadAsync(() =>
                    NativeMethods.GetWindowThreadId(owner) == NativeMethods.GetCurrentThreadId()
                        ? (true, Track(plan, owner, physicalPoint, ownerIsShellWindow: false))
                        : (false, 0u)).ConfigureAwait(false);
            }

            if (!shown)
            {
                owner = 0;
                command = await _thread.InvokeAsync(() => Track(plan, _thread.ShellWindow, physicalPoint, ownerIsShellWindow: true)).ConfigureAwait(false);
            }

            if (command == 0 || !plan.TryGetItem(command, out var item))
            {
                return NativeError.Cancelled(Capability, "The context menu was dismissed.");
            }

            Dispatch(item, windowId, owner);
            return item.Id;
        }
        catch (Exception ex)
        {
            return NativeError.FromException(Capability, ex);
        }
    }

    /// <summary>Shows a popup menu on the message thread for a notification-area icon (called by the tray service).</summary>
    internal void ShowTrayMenu(NativeMenu menu, nint shellWindow, POINT anchor)
    {
        MenuPlan plan;
        try
        {
            plan = Win32MenuPlanner.Plan(menu);
        }
        catch (ArgumentException ex)
        {
            LogMenuFailed(_logger, ex);
            return;
        }

        var command = Track(plan, shellWindow, anchor, ownerIsShellWindow: true, alreadyPhysical: true, isTrayMenu: true);
        if (command != 0 && plan.TryGetItem(command, out var item))
        {
            Dispatch(item, windowId: null, ownerWindow: 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Menus attached to windows are owned (and destroyed) by those windows. Subclasses remove themselves on the next
        // message once the instance is unregistered, so nothing can call into a disposed service.
        _ = s_instances.TryRemove(_instanceKey, out _);
        lock (_gate)
        {
            _menuBars.Clear();
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Dispatch(NativeMenuItem item, string? windowId, nint ownerWindow)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteRoleAsync(item.Role, ownerWindow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRoleFailed(_logger, item.Role, ex);
            }

            if (item.OnInvoked is { } callback)
            {
                try
                {
                    await callback().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogCallbackFailed(_logger, item.Id, ex);
                }
            }

            try
            {
                ItemInvoked?.Invoke(this, new MenuItemInvokedEventArgs(item, windowId));
            }
            catch (Exception ex)
            {
                LogCallbackFailed(_logger, item.Id, ex);
            }
        });
    }

    private static unsafe uint Track(MenuPlan plan, nint owner, POINT? point, bool ownerIsShellWindow, bool alreadyPhysical = false, bool isTrayMenu = false)
    {
        using var menu = Win32MenuFactory.Create(plan, menuBar: false);
        var previousForeground = NativeMethods.GetForegroundWindow();
        POINT position;
        if (point is { } requested)
        {
            position = requested;
            if (!alreadyPhysical)
            {
                // Convert to the owner's coordinate space (identity for per-monitor-aware windows).
                _ = NativeMethods.PhysicalToLogicalPointForPerMonitorDPI(owner, &position);
            }
        }
        else if (!NativeMethods.GetCursorPos(&position))
        {
            return 0;
        }

        // The owner must be foreground or the menu does not close when the user clicks elsewhere.
        _ = NativeMethods.SetForegroundWindow(owner);
        var command = NativeMethods.TrackPopupMenuEx(
            menu.DangerousGetHandle(),
            Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_NONOTIFY | (isTrayMenu ? Win32.TPM_BOTTOMALIGN : 0),
            position.X,
            position.Y,
            owner,
            0);
        if (ownerIsShellWindow)
        {
            // Documented workaround (KB135788): make the menu work correctly the next time it is shown.
            _ = NativeMethods.PostMessageW(owner, Win32.WM_NULL, 0, 0);

            // An in-app menu borrowed the foreground for the hidden owner: hand it back to the app window.
            if (!isTrayMenu && previousForeground != 0 && previousForeground != owner && NativeMethods.GetForegroundWindow() == owner)
            {
                _ = NativeMethods.SetForegroundWindow(previousForeground);
            }
        }

        return command > 0 ? (uint)command : 0;
    }

    private unsafe NativeResult AttachMenuBar(nint hwnd, MenuPlan? plan, string? windowId)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, "The window no longer exists.");
        }

        if (NativeMethods.GetWindowThreadId(hwnd) != NativeMethods.GetCurrentThreadId())
        {
            return NativeError.Unavailable(
                Capability,
                NativeErrorCodes.HostNotReady,
                "Window menus must be attached on the thread that owns the window. Call from the UI thread or register the " +
                "IWindowsHostBridge of your host adapter (WPF, MAUI) so the adapter can dispatch.");
        }

        MenuBar? existing;
        lock (_gate)
        {
            _ = _menuBars.TryGetValue(hwnd, out existing);
        }

        if (plan is null)
        {
            if (existing is not null)
            {
                _ = NativeMethods.SetMenu(hwnd, 0);
                _ = NativeMethods.DrawMenuBar(hwnd);
                DestroyDetachedMenu(existing.Menu);
                _ = NativeMethods.RemoveWindowSubclass(hwnd, &SubclassProc, SubclassId);
                lock (_gate)
                {
                    _ = _menuBars.Remove(hwnd);
                }
            }

            return NativeResult.Ok;
        }

        using var menu = Win32MenuFactory.Create(plan, menuBar: true);
        if (existing is null && !NativeMethods.SetWindowSubclass(hwnd, &SubclassProc, SubclassId, _instanceKey))
        {
            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, "SetWindowSubclass failed; the window cannot receive menu commands.");
        }

        if (!NativeMethods.SetMenu(hwnd, menu.DangerousGetHandle()))
        {
            var error = Marshal.GetLastPInvokeError();
            if (existing is null)
            {
                _ = NativeMethods.RemoveWindowSubclass(hwnd, &SubclassProc, SubclassId);
            }

            return NativeError.Failed(Capability, NativeErrorCodes.PlatformError, $"SetMenu failed with Win32 error {error} (child windows cannot have menus).");
        }

        // From here on the window owns the menu and destroys it with itself.
        var attached = menu.Detach();
        _ = NativeMethods.DrawMenuBar(hwnd);
        if (existing is not null)
        {
            DestroyDetachedMenu(existing.Menu);
        }

        lock (_gate)
        {
            _menuBars[hwnd] = new MenuBar(attached, plan, windowId);
        }

        return NativeResult.Ok;
    }

    private static void DestroyDetachedMenu(nint menu)
    {
        if (NativeMethods.IsMenu(menu))
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private bool TryInvokeMenuBarCommand(nint hwnd, uint commandId)
    {
        MenuBar? bar;
        lock (_gate)
        {
            _ = _menuBars.TryGetValue(hwnd, out bar);
        }

        if (bar is null || !bar.Plan.TryGetItem(commandId, out var item))
        {
            return false;
        }

        Dispatch(item, bar.WindowId, hwnd);
        return true;
    }

    private void OnWindowDestroyed(nint hwnd)
    {
        lock (_gate)
        {
            _ = _menuBars.Remove(hwnd);
        }
    }

    private NativeResult<nint> ResolveWindow(string? windowId)
    {
        if (windowId is null)
        {
            var main = _bridge.MainWindowHandle;
            return main != 0
                ? main
                : NativeError.Unavailable(Capability, NativeErrorCodes.HostNotReady, "The application has no main window yet.");
        }

        var window = _services?.GetService<IWindowManager>()?.FindWindow(windowId);
        return window is not null && window.TryGetPlatformHandle(out var handle) && handle != 0
            ? handle
            : NativeError.Unavailable(Capability, NativeErrorCodes.NotFound, $"No open window has the id '{windowId}'.");
    }

    private async Task ExecuteRoleAsync(NativeMenuRole role, nint ownerWindow)
    {
        var main = _bridge.MainWindowHandle;
        var target = ownerWindow != 0 ? ownerWindow : main;
        if (target == 0)
        {
            return;
        }

        switch (role)
        {
            case NativeMenuRole.Quit:
                if (main != 0)
                {
                    _ = NativeMethods.PostMessageW(main, Win32.WM_CLOSE, 0, 0);
                }

                break;
            case NativeMenuRole.Close:
                _ = NativeMethods.PostMessageW(target, Win32.WM_CLOSE, 0, 0);
                break;
            case NativeMenuRole.Minimize:
                _ = NativeMethods.ShowWindowAsync(target, Win32.SW_MINIMIZE);
                break;
            case NativeMenuRole.Zoom:
                _ = NativeMethods.ShowWindowAsync(target, NativeMethods.IsZoomed(target) ? Win32.SW_RESTORE : Win32.SW_MAXIMIZE);
                break;
            case NativeMenuRole.Hide:
                _ = NativeMethods.ShowWindowAsync(main, Win32.SW_HIDE);
                break;
            case NativeMenuRole.ShowAll:
                _ = NativeMethods.ShowWindowAsync(main, Win32.SW_SHOW);
                break;
            case NativeMenuRole.ToggleFullScreen:
                _ = await _bridge.RunOnUIThreadAsync(() =>
                {
                    var controller = new Win32WindowController(target);
                    return controller.SetState(controller.IsFullScreen ? NativeWindowState.Normal : NativeWindowState.FullScreen);
                }).ConfigureAwait(false);
                break;
            case NativeMenuRole.Cut:
                SendToFocusedControl(target, Win32.WM_CUT, 0, 0);
                break;
            case NativeMenuRole.Copy:
                SendToFocusedControl(target, Win32.WM_COPY, 0, 0);
                break;
            case NativeMenuRole.Paste:
                SendToFocusedControl(target, Win32.WM_PASTE, 0, 0);
                break;
            case NativeMenuRole.Undo:
                SendToFocusedControl(target, Win32.WM_UNDO, 0, 0);
                break;
            case NativeMenuRole.Redo:
                SendToFocusedControl(target, Win32.EM_REDO, 0, 0);
                break;
            case NativeMenuRole.SelectAll:
                SendToFocusedControl(target, Win32.EM_SETSEL, 0, -1);
                break;
            default:
                // About, Preferences, HideOthers, WindowMenu, HelpMenu and None behave as normal items.
                break;
        }
    }

    private static unsafe void SendToFocusedControl(nint window, uint message, nint wParam, nint lParam)
    {
        var threadId = NativeMethods.GetWindowThreadId(window);
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (threadId == 0 || !NativeMethods.GetGUIThreadInfo(threadId, &info))
        {
            return;
        }

        var focus = info.hwndFocus != 0 ? info.hwndFocus : info.hwndActive;
        if (focus != 0)
        {
            nint result;
            _ = NativeMethods.SendMessageTimeoutW(focus, message, wParam, lParam, Win32.SMTO_ABORTIFHUNG, 1000, &result);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        try
        {
            if (s_instances.TryGetValue(refData, out var service))
            {
                if (message == Win32.WM_COMMAND && Win32.HiWord(wParam) == 0 && lParam == 0 &&
                    service.TryInvokeMenuBarCommand(hwnd, Win32.LoWord(wParam)))
                {
                    return 0;
                }

                if (message == Win32.WM_NCDESTROY)
                {
                    service.OnWindowDestroyed(hwnd);
                    _ = NativeMethods.RemoveWindowSubclass(hwnd, &SubclassProc, subclassId);
                }
            }
            else
            {
                // The owning service was disposed: detach.
                _ = NativeMethods.RemoveWindowSubclass(hwnd, &SubclassProc, subclassId);
            }
        }
        catch
        {
            // Exceptions must never cross the native boundary.
        }

        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Error, Message = "The callback of menu item {ItemId} failed.")]
    private static partial void LogCallbackFailed(ILogger logger, string itemId, Exception exception);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "The standard menu role {Role} failed.")]
    private static partial void LogRoleFailed(ILogger logger, NativeMenuRole role, Exception exception);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "A notification-area menu could not be shown.")]
    private static partial void LogMenuFailed(ILogger logger, Exception exception);

    private sealed record MenuBar(nint Menu, MenuPlan Plan, string? WindowId);
}
