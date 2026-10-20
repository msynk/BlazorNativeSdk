using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// Base class for <see cref="INativeWindow"/> implementations of Win32-backed host windows (WPF, Windows Forms, WinUI).
/// Every member is implemented over a <see cref="Win32WindowController"/>; derived classes supply the HWND, marshal to the
/// window's UI thread and raise the events from their framework's notifications.
/// </summary>
/// <remarks>
/// Members may be called from any thread: mutating operations run through <see cref="InvokeOnUIThreadAsync"/>. Override
/// individual operations to use framework-native behavior (for example WPF's <c>Window.Title</c> or WinUI's full-screen
/// presenter) where it plays better with the framework's own state.
/// </remarks>
public abstract class Win32NativeWindowBase : INativeWindow
{
    private Win32WindowController? _controller;

    /// <summary>Initializes the window.</summary>
    /// <param name="id">The window id.</param>
    /// <param name="isMainWindow">Whether this is the application's main window.</param>
    protected Win32NativeWindowBase(string id, bool isMainWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        IsMainWindow = isMainWindow;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public bool IsMainWindow { get; }

    /// <inheritdoc />
    public virtual string Title => Controller?.Title ?? string.Empty;

    /// <inheritdoc />
    public virtual NativeWindowState State => Controller?.State ?? NativeWindowState.Normal;

    /// <inheritdoc />
    public virtual WindowBounds Bounds => Controller?.Bounds ?? default;

    /// <inheritdoc />
    public virtual bool IsFocused => Controller?.IsFocused ?? false;

    /// <inheritdoc />
    public virtual bool IsVisible => Controller?.IsVisible ?? false;

    /// <inheritdoc />
    public virtual WindowOperations SupportedOperations => WindowOperations.All;

    /// <summary>The HWND of the window, or <c>0</c> before it is created / after it is destroyed.</summary>
    protected abstract nint WindowHandle { get; }

    /// <summary>The controller for <see cref="WindowHandle"/>, or <see langword="null"/> when there is no window.</summary>
    protected Win32WindowController? Controller
    {
        get
        {
            var handle = WindowHandle;
            if (handle == 0)
            {
                return null;
            }

            var controller = _controller;
            if (controller is null || controller.Handle != handle)
            {
                controller = new Win32WindowController(handle);
                _controller = controller;
            }

            return controller;
        }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? BoundsChanged;

    /// <inheritdoc />
    public event EventHandler? FocusChanged;

    /// <inheritdoc />
    public event EventHandler? VisibilityChanged;

    /// <inheritdoc />
    public event EventHandler<WindowClosingEventArgs>? Closing;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return RunAsync(c => c.SetTitle(title), cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default) =>
        RunAsync(c => c.SetBounds(bounds), cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> SetStateAsync(NativeWindowState state, CancellationToken cancellationToken = default) =>
        RunAsync(c => c.SetState(state), cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> SetAlwaysOnTopAsync(bool alwaysOnTop, CancellationToken cancellationToken = default) =>
        RunAsync(c => c.SetAlwaysOnTop(alwaysOnTop), cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> FocusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(static c => c.Focus(), cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        RunAsync(c => c.SetVisible(visible), cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<NativeResult> CloseAsync(CancellationToken cancellationToken = default) =>
        RunAsync(static c => c.Close(), cancellationToken);

    /// <summary>Captures the placement for persistence (see <see cref="Win32WindowController.ToPlacement"/>).</summary>
    public virtual WindowPlacement? GetPlacement() => Controller?.ToPlacement();

    /// <summary>Applies a persisted placement on the UI thread (see <see cref="Win32WindowController.ApplyPlacement"/>).</summary>
    public virtual ValueTask<NativeResult> ApplyPlacementAsync(WindowPlacement placement, bool applyState = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return RunAsync(c => c.ApplyPlacement(placement, applyState), cancellationToken);
    }

    /// <inheritdoc />
    public virtual bool TryGetPlatformHandle(out nint handle)
    {
        handle = WindowHandle;
        return handle != 0;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the window's UI thread. The default runs inline; derived classes dispatch to their
    /// framework's dispatcher (and run inline when already on the UI thread).
    /// </summary>
    protected virtual Task InvokeOnUIThreadAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }

    /// <summary>Runs a controller operation on the UI thread and converts failures into results.</summary>
    protected async ValueTask<NativeResult> RunAsync(Func<Win32WindowController, NativeResult> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var result = NativeResult.Ok;
        try
        {
            await InvokeOnUIThreadAsync(() =>
            {
                var controller = Controller;
                result = controller is null
                    ? NativeError.Unavailable(NativeCapabilities.Windows, NativeErrorCodes.HostNotReady, $"Window '{Id}' has no native handle.")
                    : operation(controller);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return NativeError.FromException(NativeCapabilities.Windows, ex);
        }

        return result;
    }

    /// <summary>Raises <see cref="StateChanged"/>. Call from the framework's state notification.</summary>
    protected virtual void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raises <see cref="BoundsChanged"/>. Call when the window moves or resizes.</summary>
    protected virtual void OnBoundsChanged() => BoundsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raises <see cref="FocusChanged"/>. Call on activation and deactivation.</summary>
    protected virtual void OnFocusChanged() => FocusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raises <see cref="VisibilityChanged"/>. Call when the window is shown or hidden.</summary>
    protected virtual void OnVisibilityChanged() => VisibilityChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raises <see cref="Closing"/>.</summary>
    /// <returns><see langword="true"/> when a handler cancelled the close; the derived class must then keep the window open.</returns>
    protected virtual bool OnClosing()
    {
        var args = new WindowClosingEventArgs();
        Closing?.Invoke(this, args);
        return args.Cancel;
    }

    /// <summary>Raises <see cref="Closed"/>. Call after the window was destroyed.</summary>
    protected virtual void OnClosed()
    {
        _controller = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
