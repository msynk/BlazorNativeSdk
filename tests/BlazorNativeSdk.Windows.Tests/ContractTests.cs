using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

/// <summary>Runs the capability contracts against the real Windows implementations.</summary>
public sealed class ContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-windows-tests", Guid.NewGuid().ToString("N"));
    private readonly string _appId = "bns-contract-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _appDirectories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dpapi_secure_storage_satisfies_the_contract()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.contract");

        await CapabilityContracts.VerifySecureStorageAsync(storage, Ct);
        await CapabilityContracts.VerifyStatusAsync(storage, NativeCapabilities.SecureStorage, Ct);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Win32_clipboard_satisfies_the_contract()
    {
        await using var provider = BuildProvider();
        var clipboard = provider.GetRequiredService<IClipboardService>();
        Assert.IsType<WindowsClipboardService>(clipboard);

        // The contract restores the previous clipboard text when it is done.
        await CapabilityContracts.VerifyClipboardAsync(clipboard, Ct);
    }

    [Fact]
    public async Task Windows_app_storage_satisfies_the_contract()
    {
        await using var provider = BuildProvider();
        var storage = provider.GetRequiredService<IAppStorage>();
        Assert.IsType<WindowsAppStorage>(storage);
        TrackAppDirectories(storage);

        await CapabilityContracts.VerifyAppStorageAsync(storage, Ct);
    }

    [Fact]
    public async Task Every_capability_reports_an_honest_status_with_the_windows_adapter()
    {
        await using var provider = BuildProvider();
        TrackAppDirectories(provider.GetRequiredService<IAppStorage>());
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
        var windows = 0;

        foreach (var capability in registry.Capabilities)
        {
            var implementation = registry.GetProvider(capability)!;
            await CapabilityContracts.VerifyStatusAsync(implementation, capability, Ct);
            if (implementation.GetType().Assembly == typeof(DpapiSecureStorage).Assembly)
            {
                windows++;
            }
        }

        // Screens, shortcuts, menus, tray, secure storage, app storage, biometrics, clipboard, files, camera, barcode,
        // share, notifications, connectivity and permissions.
        Assert.Equal(15, windows);
    }

    public void Dispose()
    {
        foreach (var directory in _appDirectories.Append(_directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddBlazorNativeSdk(sdk => sdk
            .Configure(o => o.AppId = _appId)
            .AddWindows(o =>
            {
                // Keep tests free of machine-wide side effects.
                o.RegisterAppUserModelId = false;
                o.SetProcessAppUserModelId = false;
                o.SecureStorageDirectory = _directory;
            }));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    // The app-specific folders created for the unique app id (never the user's Documents or Downloads).
    private void TrackAppDirectories(IAppStorage storage)
    {
        foreach (var directory in new[] { AppDirectory.AppData, AppDirectory.Cache, AppDirectory.Temp })
        {
            _appDirectories.Add(storage.GetDirectory(directory).GetValueOrThrow());
        }
    }
}
