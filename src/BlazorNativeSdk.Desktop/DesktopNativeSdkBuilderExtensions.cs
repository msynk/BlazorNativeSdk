using System.Runtime.Versioning;
using BlazorNativeSdk.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk;

/// <summary>Registers the host-neutral desktop services.</summary>
public static class DesktopNativeSdkBuilderExtensions
{
    /// <summary>
    /// Adds the desktop services shared by every desktop host (WPF, WinForms, MAUI on Windows/macOS, Photino, custom hosts):
    /// physical app storage, a file-based state store, system connectivity, the in-process background work scheduler and
    /// HTTP transfers (both running while the app is in the background), plus <see cref="IIpcService"/>,
    /// <see cref="ISingleInstanceService"/>, <see cref="IDesktopFileOperations"/> and <see cref="IProtocolRegistrationService"/>.
    /// </summary>
    /// <remarks>
    /// This method does not declare the host; host packages (for example <c>AddWpf()</c>) call
    /// <see cref="INativeSdkBuilder.SetHost"/>. Call it before host-specific adapters so they can override individual
    /// capabilities.
    /// </remarks>
    /// <param name="builder">The SDK builder.</param>
    /// <param name="configure">Configures <see cref="DesktopNativeOptions"/>.</param>
    [UnsupportedOSPlatform("browser")]
    public static INativeSdkBuilder AddDesktop(this INativeSdkBuilder builder, Action<DesktopNativeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddPhysicalAppStorage();
        builder.AddFileStateStore();
        builder.AddSystemConnectivity();
        builder.AddInProcessBackgroundWork(o => o.RunWhileInBackground = true);
        builder.AddHttpTransfers(o => o.ContinuesInBackground = true);

        var options = builder.Services.AddOptions<DesktopNativeOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.AddCapability<IIpcService, DesktopIpcService>(NativeCapabilities.Ipc);
        builder.AddCapability<ISingleInstanceService, DesktopSingleInstanceService>(NativeCapabilities.SingleInstance);
        builder.AddCapability<IDesktopFileOperations, DesktopFileOperations>(NativeCapabilities.DesktopFiles);
        builder.AddCapability<IProtocolRegistrationService, DesktopProtocolRegistrationService>(NativeCapabilities.ProtocolRegistration);
        return builder;
    }
}
