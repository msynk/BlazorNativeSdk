using System.Diagnostics.CodeAnalysis;
using System.Windows;
using BlazorNativeSdk.Desktop;

namespace BlazorNativeSdk.Wpf;

/// <summary>
/// The WPF window properties derived from <see cref="WindowOptions"/> and an optional persisted placement. Pure mapping
/// logic, kept free of UI objects so it can be unit tested.
/// </summary>
internal sealed record WpfWindowSettings
{
    /// <summary>The maximum length of a window id.</summary>
    public const int MaxIdLength = 256;

    /// <summary>The window title.</summary>
    public required string Title { get; init; }

    /// <summary><see cref="FrameworkElement.Width"/> (<see cref="double.NaN"/> = system default).</summary>
    public double Width { get; init; } = double.NaN;

    /// <summary><see cref="FrameworkElement.Height"/> (<see cref="double.NaN"/> = system default).</summary>
    public double Height { get; init; } = double.NaN;

    /// <summary><see cref="FrameworkElement.MinWidth"/>.</summary>
    public double MinWidth { get; init; }

    /// <summary><see cref="FrameworkElement.MinHeight"/>.</summary>
    public double MinHeight { get; init; }

    /// <summary><see cref="Window.Left"/> (<see cref="double.NaN"/> = system default).</summary>
    public double Left { get; init; } = double.NaN;

    /// <summary><see cref="Window.Top"/> (<see cref="double.NaN"/> = system default).</summary>
    public double Top { get; init; } = double.NaN;

    /// <summary><see cref="Window.WindowStartupLocation"/>.</summary>
    public WindowStartupLocation StartupLocation { get; init; }

    /// <summary><see cref="Window.ResizeMode"/>.</summary>
    public ResizeMode ResizeMode { get; init; } = ResizeMode.CanResize;

    /// <summary><see cref="Window.Topmost"/>.</summary>
    public bool Topmost { get; init; }

    /// <summary>The <see cref="Window.WindowState"/> the window is shown with.</summary>
    public WindowState WindowState { get; init; }

    /// <summary>The effective initial state, including <see cref="NativeWindowState.FullScreen"/>.</summary>
    public NativeWindowState InitialState { get; init; }

    /// <summary>Whether the window switches to full screen once it is shown (WPF has no full-screen state).</summary>
    public bool EnterFullScreen => InitialState == NativeWindowState.FullScreen;

    /// <summary>
    /// The requested position (and size, <see cref="double.NaN"/> when unspecified) in the SDK's DIP convention, applied
    /// to the native window after its handle exists so mixed-DPI layouts are honored. <see langword="null"/> when the
    /// options set no position or a persisted placement takes precedence.
    /// </summary>
    public WindowBounds? ExplicitBounds { get; init; }

    /// <summary>The persisted placement to restore, if any.</summary>
    public WindowPlacement? Placement { get; init; }

    /// <summary>The <c>BlazorWebView.StartPath</c>, or <see langword="null"/> for the default (<c>/</c>).</summary>
    public string? StartPath { get; init; }

    /// <summary>Validates <paramref name="options"/> and maps them to WPF settings.</summary>
    /// <param name="options">The window options.</param>
    /// <param name="placement">A persisted placement (already validated against the connected displays), if any.</param>
    /// <param name="defaultTitle">The title used when <see cref="WindowOptions.Title"/> is not set.</param>
    /// <param name="hasOwner">Whether the window has an owner window (centers on the owner instead of the screen).</param>
    public static NativeResult<WpfWindowSettings> Create(WindowOptions options, WindowPlacement? placement, string defaultTitle, bool hasOwner)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(options) is { } error)
        {
            return error;
        }

        string? startPath = null;
        if (options.RootComponent is null && options.Route is not null)
        {
            if (!TryNormalizeRoute(options.Route, out startPath))
            {
                return Invalid($"The route '{options.Route}' is not an app-relative path (for example '/settings').");
            }
        }

        if (placement is not null && (placement.Bounds.Width <= 0 || placement.Bounds.Height <= 0 ||
                                      !IsFinite(placement.Bounds.X) || !IsFinite(placement.Bounds.Y)))
        {
            placement = null;
        }

        var state = ResolveInitialState(options.InitialState, placement);
        var settings = new WpfWindowSettings
        {
            Title = options.Title ?? defaultTitle,
            MinWidth = options.MinWidth ?? 0,
            MinHeight = options.MinHeight ?? 0,
            ResizeMode = options.Resizable ? ResizeMode.CanResize : ResizeMode.CanMinimize,
            Topmost = options.AlwaysOnTop,
            InitialState = state,
            WindowState = ToWpfState(state),
            StartPath = startPath,
            Placement = placement,
        };

        if (placement is not null)
        {
            var bounds = placement.Bounds;
            return settings with
            {
                Left = bounds.X,
                Top = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                StartupLocation = WindowStartupLocation.Manual,
            };
        }

        settings = settings with
        {
            Width = options.Width ?? double.NaN,
            Height = options.Height ?? double.NaN,
        };

        if (options.X is null && options.Y is null)
        {
            return settings with { StartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
        }

        settings = settings with
        {
            Left = options.X ?? double.NaN,
            Top = options.Y ?? double.NaN,
            StartupLocation = WindowStartupLocation.Manual,
        };

        return options.X is { } x && options.Y is { } y
            ? settings with { ExplicitBounds = new WindowBounds(x, y, options.Width ?? double.NaN, options.Height ?? double.NaN) }
            : settings;
    }

    /// <summary>
    /// Resolves the initial state. A persisted placement reflects the user's last choice and wins over
    /// <see cref="WindowOptions.InitialState"/>, except that an explicit <see cref="NativeWindowState.Minimized"/> request
    /// (start in the background) is always honored. Persisted placements never restore minimized.
    /// </summary>
    public static NativeWindowState ResolveInitialState(NativeWindowState requested, WindowPlacement? placement)
    {
        if (requested == NativeWindowState.Minimized || placement is null)
        {
            return requested;
        }

        return placement.State == NativeWindowState.Minimized ? NativeWindowState.Normal : placement.State;
    }

    /// <summary>Maps a native state to the WPF state a window is shown with (full screen starts as normal).</summary>
    public static WindowState ToWpfState(NativeWindowState state) => state switch
    {
        NativeWindowState.Minimized => WindowState.Minimized,
        NativeWindowState.Maximized => WindowState.Maximized,
        _ => WindowState.Normal,
    };

    /// <summary>Maps a WPF state to the native state.</summary>
    public static NativeWindowState FromWpfState(WindowState state) => state switch
    {
        WindowState.Minimized => NativeWindowState.Minimized,
        WindowState.Maximized => NativeWindowState.Maximized,
        _ => NativeWindowState.Normal,
    };

    /// <summary>
    /// Normalizes an app route for <c>BlazorWebView.StartPath</c>: adds a leading slash and rejects absolute URLs,
    /// protocol-relative paths (<c>//host</c>), backslashes and control characters so a window can only load app content.
    /// </summary>
    public static bool TryNormalizeRoute(string? route, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (route is null)
        {
            return false;
        }

        var value = route.Trim();
        if (value.Length == 0)
        {
            normalized = "/";
            return true;
        }

        if (value.Contains('\\', StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            return false;
        }

        if (!value.StartsWith('/'))
        {
            // "settings" is relative; "https:x", "javascript:x" or "C:/x" are not routes.
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            var boundary = value.IndexOfAny(['/', '?', '#']);
            if (colon >= 0 && (boundary < 0 || colon < boundary))
            {
                return false;
            }

            value = "/" + value;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    /// <summary>Validates the numeric and id fields of <paramref name="options"/>.</summary>
    public static NativeError? Validate(WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Id is { } id && (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength))
        {
            return Invalid($"Window ids must be non-empty and at most {MaxIdLength} characters.");
        }

        if (options.OwnerId is not null && options.OwnerId == options.Id)
        {
            return Invalid("A window cannot own itself.");
        }

        if (!IsPositive(options.Width) || !IsPositive(options.Height))
        {
            return Invalid("Window width and height must be finite and greater than zero.");
        }

        if (options.MinWidth is { } minWidth && (!IsFinite(minWidth) || minWidth < 0) ||
            options.MinHeight is { } minHeight && (!IsFinite(minHeight) || minHeight < 0))
        {
            return Invalid("Minimum window sizes must be finite and not negative.");
        }

        if (options.X is { } x && !IsFinite(x) || options.Y is { } y && !IsFinite(y))
        {
            return Invalid("Window coordinates must be finite.");
        }

        return Enum.IsDefined(options.InitialState) ? null : Invalid($"Unknown window state '{options.InitialState}'.");
    }

    private static bool IsPositive(double? value) => value is not { } v || (IsFinite(v) && v > 0);

    private static bool IsFinite(double value) => double.IsFinite(value);

    private static NativeError Invalid(string message) =>
        NativeError.Failed(NativeCapabilities.Windows, NativeErrorCodes.InvalidArgument, message);
}
