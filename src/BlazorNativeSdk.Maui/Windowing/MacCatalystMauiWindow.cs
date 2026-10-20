#if MACCATALYST
using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.Logging;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace BlazorNativeSdk.Maui;

/// <summary>
/// <see cref="INativeWindow"/> for a MAUI window (a <c>UIWindowScene</c>) on Mac Catalyst, implemented over the MAUI window
/// properties. UIKit exposes no API to minimize, maximize, hide or keep a scene's window on top, so those operations report
/// <see cref="NativeResultStatus.Unsupported"/> (see <see cref="SupportedOperations"/>). Moving and resizing use MAUI's
/// <c>UIWindowSceneGeometryPreferencesMac</c> support, available on macOS 13 (Mac Catalyst 16) and later.
/// </summary>
/// <remarks>
/// State is always reported as <see cref="NativeWindowState.Normal"/>. <see cref="INativeWindow.Closing"/> is raised (and can be
/// cancelled) for closes requested through <see cref="CloseAsync"/>; a user closing the window with its close button cannot be
/// intercepted by UIKit and only raises <see cref="INativeWindow.Closed"/>.
/// </remarks>
internal sealed partial class MacCatalystMauiWindow : IMauiNativeWindow
{
    private readonly MauiUIDispatcher _dispatcher;
    private readonly ILogger _logger;
    private int _closed;
    private int _reportedVisible;

    /// <summary>Creates the wrapper. Must be called on the UI thread.</summary>
    public MacCatalystMauiWindow(TrackedWindow tracked, MauiUIDispatcher dispatcher, ILogger logger)
    {
        Tracked = tracked;
        _dispatcher = dispatcher;
        _logger = logger;
        _reportedVisible = IsVisible ? 1 : 0;
    }

    /// <summary><see langword="true"/> when the OS supports moving and resizing scenes programmatically (macOS 13+).</summary>
    public static bool SupportsGeometry => OperatingSystem.IsMacCatalystVersionAtLeast(16);

    /// <inheritdoc />
    public TrackedWindow Tracked { get; }

    /// <inheritdoc />
    public string Id => Tracked.Id;

    /// <inheritdoc />
    public string Title => Tracked.Window.Title ?? string.Empty;

    /// <inheritdoc />
    public NativeWindowState State => NativeWindowState.Normal;

    /// <inheritdoc />
    public WindowBounds Bounds
    {
        get
        {
            var window = Tracked.Window;
            return new WindowBounds(Finite(window.X), Finite(window.Y), Finite(window.Width), Finite(window.Height));
        }
    }

    /// <inheritdoc />
    public bool IsFocused => Tracked.IsActive;

    /// <inheritdoc />
    public bool IsVisible => !Tracked.IsClosed && !Tracked.IsStopped;

    /// <inheritdoc />
    public bool IsMainWindow => Tracked.IsMain;

    /// <inheritdoc />
    public WindowOperations SupportedOperations =>
        WindowOperations.SetTitle | WindowOperations.Focus | WindowOperations.Close |
        (SupportsGeometry ? WindowOperations.Move | WindowOperations.Resize : WindowOperations.None);

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? BoundsChanged;

    /// <inheritdoc />
    public event EventHandler? FocusChanged;

    /// <inheritdoc />
    /// <remarks>Raised when the window's scene moves to the background (stopped) or returns to the foreground (resumed).</remarks>
    public event EventHandler? VisibilityChanged;

    /// <inheritdoc />
    public event EventHandler<WindowClosingEventArgs>? Closing;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public event EventHandler? CloseApproved;

    /// <inheritdoc />
    public ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return RunAsync(() =>
        {
            Tracked.Window.Title = title;
            return NativeResult.Ok;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default)
    {
        if (!SupportsGeometry)
        {
            return ValueTask.FromResult<NativeResult>(Unsupported("Moving and resizing windows requires macOS 13 or later."));
        }

        if (!IsValid(bounds))
        {
            return ValueTask.FromResult<NativeResult>(NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, "Window bounds must be finite with a positive width and height."));
        }

        return RunAsync(() =>
        {
            ApplyGeometry(Tracked.Window, bounds);
            return NativeResult.Ok;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(state == NativeWindowState.Normal
            ? NativeResult.Ok
            : (NativeResult)Unsupported($"UIKit cannot change a Mac Catalyst window to the {state} state."));

    /// <inheritdoc />
    public ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(alwaysOnTop
            ? (NativeResult)Unsupported("UIKit cannot keep a Mac Catalyst window above other windows.")
            : NativeResult.Ok);

    /// <inheritdoc />
    public ValueTask<NativeResult> FocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            if (MauiApplication.Current is not { } application)
            {
                return NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, "No MAUI application is running.");
            }

            // Requests activation of the window's scene session, which brings it to the front.
            application.ActivateWindow(Tracked.Window);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(visible
            ? NativeResult.Ok
            : (NativeResult)Unsupported("UIKit cannot hide a Mac Catalyst window; close it instead."));

    /// <inheritdoc />
    public ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            if (MauiApplication.Current is not { } application)
            {
                return NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, "No MAUI application is running.");
            }

            var args = new WindowClosingEventArgs();
            Guard(() => Closing?.Invoke(this, args), nameof(Closing));
            if (args.Cancel)
            {
                return NativeError.Cancelled(NativeCapabilities.Windows, $"A Closing handler kept window '{Id}' open.");
            }

            Guard(() => CloseApproved?.Invoke(this, EventArgs.Empty), nameof(CloseApproved));
            application.CloseWindow(Tracked.Window);
            return NativeResult.Ok;
        }, cancellationToken);

    /// <inheritdoc />
    public bool TryGetPlatformHandle(out nint handle)
    {
        handle = Tracked.IsClosed ? 0 : Tracked.PlatformHandle;
        return handle != 0;
    }

    /// <inheritdoc />
    public WindowPlacement? CapturePlacement()
    {
        var bounds = Bounds;
        return IsValid(bounds) ? new WindowPlacement { Bounds = bounds, State = NativeWindowState.Normal } : null;
    }

    /// <inheritdoc />
    public void NotifyStateChanged() => Guard(() => StateChanged?.Invoke(this, EventArgs.Empty), nameof(StateChanged));

    /// <inheritdoc />
    public void NotifyBoundsChanged() => Guard(() => BoundsChanged?.Invoke(this, EventArgs.Empty), nameof(BoundsChanged));

    /// <inheritdoc />
    public void NotifyFocusChanged() => Guard(() => FocusChanged?.Invoke(this, EventArgs.Empty), nameof(FocusChanged));

    /// <inheritdoc />
    public void NotifyVisibilityChanged()
    {
        var visible = IsVisible ? 1 : 0;
        if (Interlocked.Exchange(ref _reportedVisible, visible) != visible)
        {
            Guard(() => VisibilityChanged?.Invoke(this, EventArgs.Empty), nameof(VisibilityChanged));
        }
    }

    /// <inheritdoc />
    public void NotifyClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Guard(() => Closed?.Invoke(this, EventArgs.Empty), nameof(Closed));
        }
    }

    /// <summary>Sets the MAUI window geometry (MAUI requests it from the scene only when X, Y, width and height are all set). UI thread.</summary>
    internal static void ApplyGeometry(Microsoft.Maui.Controls.Window window, WindowBounds bounds)
    {
        window.X = bounds.X;
        window.Y = bounds.Y;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
    }

    /// <summary>Returns whether <paramref name="bounds"/> is usable.</summary>
    internal static bool IsValid(WindowBounds bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && bounds.Width > 0 && bounds.Height > 0 &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static NativeError Unsupported(string reason) => NativeError.Unsupported(NativeCapabilities.Windows, reason);

    private async ValueTask<NativeResult> RunAsync(Func<NativeResult> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Tracked.IsClosed)
        {
            return NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.NotFound, "The window no longer exists.");
        }

        try
        {
            return await _dispatcher.InvokeAsync(operation).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NativeError.FromException(NativeCapabilities.Windows, ex);
        }
    }

    private void Guard(Action action, string eventName)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHandlerFailed(_logger, eventName, Id, ex);
        }
    }

    [LoggerMessage(EventId = 8302, Level = LogLevel.Error, Message = "A {Event} handler of window '{WindowId}' failed.")]
    private static partial void LogHandlerFailed(ILogger logger, string @event, string windowId, Exception exception);
}
#endif
