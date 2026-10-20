using BlazorNativeSdk.Desktop.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop;

/// <summary>Registers custom URL schemes for the current user at runtime.</summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows (unpackaged): <c>HKCU\Software\Classes\{scheme}</c>. MSIX-packaged apps report Unsupported and declare
/// the protocol in <c>Package.appxmanifest</c>.</item>
/// <item>Linux: a hidden desktop entry in <c>~/.local/share/applications</c> set as the default handler with
/// <c>xdg-mime</c>. Flatpak/Snap apps report Unsupported.</item>
/// <item>macOS: Unsupported; declare <c>CFBundleURLTypes</c> in the app bundle's <c>Info.plist</c>.</item>
/// </list>
/// Schemes are validated against RFC 3986, and browser/OS schemes (<c>http</c>, <c>file</c>, <c>ms-*</c>, ...) are refused.
/// A scheme owned by another application is not replaced unless
/// <see cref="DesktopNativeOptions.OverwriteForeignProtocolRegistrations"/> is set. Register the schemes in
/// <see cref="DeepLinkOptions.Schemes"/> as well so incoming URLs map to routes.
/// </remarks>
public sealed partial class DesktopProtocolRegistrationService : IProtocolRegistrationService
{
    private readonly NativeSdkOptions _sdkOptions;
    private readonly DesktopNativeOptions _options;
    private readonly ILogger<DesktopProtocolRegistrationService> _logger;

    /// <summary>Creates the service.</summary>
    public DesktopProtocolRegistrationService(IOptions<NativeSdkOptions> sdkOptions, IOptions<DesktopNativeOptions> options, ILogger<DesktopProtocolRegistrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(sdkOptions);
        ArgumentNullException.ThrowIfNull(options);
        _sdkOptions = sdkOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.ProtocolRegistration;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var reason = GetPlatformError();
        return ValueTask.FromResult(reason is null
            ? CapabilityStatus.Available(Capability, DesktopAdapter.Name, OperatingSystem.IsWindows() ? "registry" : "xdg-mime")
            : CapabilityStatus.FromError(reason, DesktopAdapter.Name));
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> RegisterAsync(string scheme, ProtocolRegistrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProtocolPolicy.TryValidateScheme(scheme, out var normalized, out var invalidScheme))
        {
            return invalidScheme;
        }

        if (GetPlatformError() is { } platformError)
        {
            return platformError;
        }

        if (!ProtocolPolicy.TryResolveExecutable(options?.ExecutablePath, out var executable, out var invalidExecutable))
        {
            return invalidExecutable;
        }

        if (!ProtocolPolicy.TryValidateTemplate(options?.ArgumentsTemplate, out var template, out var invalidTemplate))
        {
            return invalidTemplate;
        }

        var displayName = string.IsNullOrWhiteSpace(options?.DisplayName) ? _sdkOptions.DisplayName : options.DisplayName;
        var result = await NativeOperation.RunAsync(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsProtocolRegistrar.Register(normalized, executable, template, displayName, _sdkOptions.AppId, _options.OverwriteForeignProtocolRegistrations);
            }

            if (OperatingSystem.IsLinux())
            {
                return await LinuxProtocolRegistrar.RegisterAsync(normalized, executable, template, displayName, _sdkOptions.AppId, _options.OverwriteForeignProtocolRegistrations, token).ConfigureAwait(false);
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            LogRegistered(_logger, normalized, executable);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<NativeResult> UnregisterAsync(string scheme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProtocolPolicy.TryValidateScheme(scheme, out var normalized, out var invalidScheme))
        {
            return invalidScheme;
        }

        if (GetPlatformError() is { Status: NativeResultStatus.Unsupported } platformError)
        {
            return platformError;
        }

        return await NativeOperation.RunAsync(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                var executable = Environment.ProcessPath ?? string.Empty;
                return WindowsProtocolRegistrar.Unregister(normalized, executable, _sdkOptions.AppId);
            }

            if (OperatingSystem.IsLinux())
            {
                return await LinuxProtocolRegistrar.UnregisterAsync(normalized, _sdkOptions.AppId, token).ConfigureAwait(false);
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Checks that the scheme's handler points to this executable (<see cref="Environment.ProcessPath"/>, or the AppImage on
    /// Linux). Registrations made with an explicit <see cref="ProtocolRegistrationOptions.ExecutablePath"/> for a different
    /// executable report <see langword="false"/>.
    /// </remarks>
    public async ValueTask<NativeResult<bool>> IsRegisteredAsync(string scheme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProtocolPolicy.TryValidateScheme(scheme, out var normalized, out var invalidScheme))
        {
            return invalidScheme;
        }

        if (GetPlatformError() is { } platformError)
        {
            return platformError;
        }

        if (!ProtocolPolicy.TryResolveExecutable(null, out var executable, out _))
        {
            // Running under the dotnet host: nothing can be registered to this process.
            return false;
        }

        return await NativeOperation.RunAsync<bool>(Capability, async token =>
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsProtocolRegistrar.IsRegistered(normalized, executable);
            }

            if (OperatingSystem.IsLinux())
            {
                return await LinuxProtocolRegistrar.IsRegisteredAsync(normalized, executable, _sdkOptions.AppId, token).ConfigureAwait(false);
            }

            return NativeError.Unsupported(Capability);
        }, cancellationToken).ConfigureAwait(false);
    }

    private NativeError? GetPlatformError()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsProtocolRegistrar.GetUnsupportedReason();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxProtocolRegistrar.GetUnsupportedReason();
        }

        if (OperatingSystem.IsMacOS())
        {
            return NativeError.Unsupported(Capability,
                "macOS registers URL schemes from the app bundle: declare CFBundleURLTypes in Info.plist.",
                NativeErrorCodes.MissingDeclaration);
        }

        return NativeError.Unsupported(Capability);
    }

    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Registered the '{Scheme}' URL scheme for {Executable}.")]
    private static partial void LogRegistered(ILogger logger, string scheme, string executable);
}
