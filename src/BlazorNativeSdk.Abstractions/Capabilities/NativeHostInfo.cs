namespace BlazorNativeSdk;

/// <summary>The operating system family the native capabilities run on.</summary>
public enum NativePlatform
{
    /// <summary>The platform is not known.</summary>
    Unknown = 0,

    /// <summary>A web browser (Blazor WebAssembly, or the client side of Blazor Server / interactive server rendering).</summary>
    Browser,

    /// <summary>Android.</summary>
    Android,

    /// <summary>iOS and iPadOS.</summary>
#pragma warning disable CA1707, IDE1006 // Matches the platform's official casing.
    iOS,
#pragma warning restore CA1707, IDE1006

    /// <summary>Mac Catalyst.</summary>
    MacCatalyst,

    /// <summary>Windows.</summary>
    Windows,

    /// <summary>macOS (AppKit hosts).</summary>
    MacOS,

    /// <summary>Linux.</summary>
    Linux,
}

/// <summary>The kind of application that hosts the Blazor renderer.</summary>
public enum NativeHostKind
{
    /// <summary>The host is not known.</summary>
    Unknown = 0,

    /// <summary>Blazor WebAssembly running in a browser.</summary>
    WebAssembly,

    /// <summary>Blazor Server / interactive server rendering. .NET runs on the server; device capabilities run in the client browser.</summary>
    Server,

    /// <summary>.NET MAUI with <c>BlazorWebView</c>.</summary>
    Maui,

    /// <summary>WPF with <c>BlazorWebView</c>.</summary>
    Wpf,

    /// <summary>Windows Forms with <c>BlazorWebView</c>.</summary>
    WinForms,

    /// <summary>Photino.Blazor.</summary>
    Photino,

    /// <summary>A custom host.</summary>
    Custom,
}

/// <summary>The device form factor.</summary>
public enum DeviceFormFactor
{
    /// <summary>Unknown form factor.</summary>
    Unknown = 0,

    /// <summary>A phone.</summary>
    Phone,

    /// <summary>A tablet.</summary>
    Tablet,

    /// <summary>A desktop or laptop computer.</summary>
    Desktop,

    /// <summary>A television.</summary>
    TV,

    /// <summary>A watch.</summary>
    Watch,
}

/// <summary>Describes the host the application runs in.</summary>
public sealed record NativeHostInfo
{
    /// <summary>Information for an unknown host.</summary>
    public static NativeHostInfo Unknown { get; } = new() { Platform = NativePlatform.Unknown, Host = NativeHostKind.Unknown };

    /// <summary>The platform on which native capabilities execute.</summary>
    public required NativePlatform Platform { get; init; }

    /// <summary>The Blazor host.</summary>
    public required NativeHostKind Host { get; init; }

    /// <summary>The form factor when known.</summary>
    public DeviceFormFactor FormFactor { get; init; }

    /// <summary>The operating system version when known.</summary>
    public Version? OperatingSystemVersion { get; init; }

    /// <summary>A human-readable host description used in diagnostics (for example "MAUI on Android 15").</summary>
    public string? Description { get; init; }

    /// <summary><see langword="true"/> when Blazor runs in a native WebView inside a native application.</summary>
    public bool IsHybrid => Host is NativeHostKind.Maui or NativeHostKind.Wpf or NativeHostKind.WinForms or NativeHostKind.Photino;

    /// <summary><see langword="true"/> when capabilities are provided by a web browser.</summary>
    public bool IsBrowser => Platform == NativePlatform.Browser;

    /// <summary><see langword="true"/> for Android and iOS.</summary>
    public bool IsMobile => Platform is NativePlatform.Android or NativePlatform.iOS;

    /// <summary><see langword="true"/> for Windows, macOS, Mac Catalyst and Linux.</summary>
    public bool IsDesktop => Platform is NativePlatform.Windows or NativePlatform.MacOS or NativePlatform.MacCatalyst or NativePlatform.Linux;

    /// <inheritdoc />
    public override string ToString() => Description ?? $"{Host} on {Platform}{(OperatingSystemVersion is null ? string.Empty : " " + OperatingSystemVersion)}";
}
