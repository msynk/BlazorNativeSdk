using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed class RegistrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-windows-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddWindows_registers_every_windows_capability_and_disposes_cleanly()
    {
        var provider = BuildProvider();
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<DefaultWindowsHostBridge>(provider.GetRequiredService<IWindowsHostBridge>());
        Assert.IsType<ActivationService>(provider.GetRequiredService<IActivationPublisher>());
        Assert.Contains(provider.GetServices<IActivationTranslator>(), t => t is WindowsToastActivationTranslator);

        INativeCapability[] capabilities =
        [
            provider.GetRequiredService<IScreenService>(),
            provider.GetRequiredService<IGlobalShortcutService>(),
            provider.GetRequiredService<INativeMenuService>(),
            provider.GetRequiredService<ITrayService>(),
            provider.GetRequiredService<ISecureStorage>(),
            provider.GetRequiredService<IAppStorage>(),
            provider.GetRequiredService<IBiometricService>(),
            provider.GetRequiredService<IClipboardService>(),
            provider.GetRequiredService<IFilePickerService>(),
            provider.GetRequiredService<ICameraService>(),
            provider.GetRequiredService<IBarcodeScanner>(),
            provider.GetRequiredService<IShareService>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IConnectivityService>(),
            provider.GetRequiredService<IPermissionService>(),
        ];

        foreach (var capability in capabilities)
        {
            Assert.Equal("BlazorNativeSdk.Windows", capability.GetType().Assembly.GetName().Name);
            var status = await capability.GetStatusAsync(ct);
            Assert.Equal(capability.Capability, status.Capability);
            Assert.True(status.Adapter == "windows", $"{capability.Capability}: {status}");
        }

        // One shared instance per implementation.
        Assert.Same(provider.GetRequiredService<ISecureStorage>(), provider.GetRequiredService<DpapiSecureStorage>());

        var registry = provider.CreateScope().ServiceProvider.GetRequiredService<ICapabilityRegistry>();
        Assert.Equal(CapabilityAvailability.Unsupported, (await registry.GetStatusAsync(NativeCapabilities.Barcode, ct)).Availability);
        Assert.Equal(CapabilityAvailability.Available, (await registry.GetStatusAsync(NativeCapabilities.Screens, ct)).Availability);

        // Containers dispose forwarded singletons twice and hosts may dispose synchronously.
        provider.Dispose();
        provider.Dispose();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Services_report_honest_results_without_ui()
    {
        await using var provider = BuildProvider();
        var ct = TestContext.Current.CancellationToken;

        var camera = provider.GetRequiredService<ICameraService>();
        var capture = await camera.CapturePhotoAsync(cancellationToken: ct);
        Assert.Equal(NativeResultStatus.Unsupported, capture.Status);
        Assert.Contains("getUserMedia", capture.Error!.Message, StringComparison.Ordinal);

        var scan = await provider.GetRequiredService<IBarcodeScanner>().ScanAsync(cancellationToken: ct);
        Assert.Equal(NativeResultStatus.Unsupported, scan.Status);

        var schedule = await provider.GetRequiredService<INotificationService>().ScheduleAsync(
            new LocalNotification { Id = "r", Title = "t" },
            new NotificationSchedule { DeliverAt = DateTimeOffset.UtcNow.AddDays(1), Repeat = NotificationRepeat.Daily },
            ct);
        Assert.Equal(NativeResultStatus.Unsupported, schedule.Status);
        Assert.True((await provider.GetRequiredService<INotificationService>().CreateChannelAsync(new NotificationChannel { Id = "c", Name = "C" }, ct)).IsSuccess);

        var permissions = provider.GetRequiredService<IPermissionService>();
        Assert.Equal(PermissionStatus.NotRequired, (await permissions.CheckAsync(NativePermissions.StorageRead, ct)).Value);
        Assert.Equal(PermissionStatus.NotRequired, (await permissions.CheckAsync(NativePermissions.Biometrics, ct)).Value);
        Assert.Equal(NativeResultStatus.Unsupported, (await permissions.CheckAsync(new NativePermission("bluetooth-scan"), ct)).Status);
        Assert.True((await permissions.CheckAsync(NativePermissions.Camera, ct)).IsSuccess);
        Assert.False((await permissions.ShouldShowRationaleAsync(NativePermissions.Camera, ct)).Value);

        var storage = provider.GetRequiredService<IAppStorage>();
        foreach (var directory in Enum.GetValues<AppDirectory>())
        {
            var path = storage.GetDirectory(directory);
            Assert.True(path.IsSuccess, path.Error?.ToString());
            Assert.True(Directory.Exists(path.Value));
        }

        Assert.EndsWith("Downloads", storage.GetDirectory(AppDirectory.Downloads).Value, StringComparison.OrdinalIgnoreCase);

        var secure = provider.GetRequiredService<ISecureStorage>();
        Assert.True((await secure.SetAsync("k", "v", ct)).IsSuccess);
        Assert.StartsWith(_directory, Directory.GetFiles(_directory).Single(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(sdk => sdk
            .Configure(o => o.AppId = "BlazorNativeSdk.Windows.Tests")
            .AddWindows(o =>
            {
                // Keep tests free of machine-wide side effects.
                o.RegisterAppUserModelId = false;
                o.SetProcessAppUserModelId = false;
                o.SecureStorageDirectory = _directory;
            }));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
