using BlazorNativeSdk.Desktop.Protocols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlazorNativeSdk.Desktop.Tests;

public sealed class ProtocolRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("myapp", "myapp")]
    [InlineData("MyApp", "myapp")]
    [InlineData("com.contoso.notes", "com.contoso.notes")]
    [InlineData("web+notes", "web+notes")]
    [InlineData("x-1", "x-1")]
    public void Valid_schemes_are_normalized(string scheme, string expected)
    {
        Assert.True(ProtocolPolicy.TryValidateScheme(scheme, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1app")]
    [InlineData("-app")]
    [InlineData("my app")]
    [InlineData("my_app")]
    [InlineData("my:app")]
    [InlineData("app/x")]
    [InlineData("app\n")]
    [InlineData("http")]
    [InlineData("HTTPS")]
    [InlineData("file")]
    [InlineData("mailto")]
    [InlineData("javascript")]
    [InlineData("data")]
    [InlineData("about")]
    [InlineData("ms-settings")]
    [InlineData("microsoft-edge")]
    [InlineData("search-ms")]
    [InlineData("shell")]
    public void Invalid_or_reserved_schemes_are_rejected(string scheme)
    {
        Assert.False(ProtocolPolicy.TryValidateScheme(scheme, out _, out var error));
        Assert.Equal(NativeErrorCodes.InvalidArgument, error.Code);
    }

    [Fact]
    public void Overlong_schemes_are_rejected() =>
        Assert.False(ProtocolPolicy.TryValidateScheme("a" + new string('b', ProtocolPolicy.MaxSchemeLength), out _, out _));

    [Theory]
    [InlineData(null, "\"%1\"")]
    [InlineData("--open-url \"%1\"", "--open-url \"%1\"")]
    [InlineData("-- \"%1\"", "-- \"%1\"")]
    public void Quoted_templates_are_accepted(string? template, string expected)
    {
        Assert.True(ProtocolPolicy.TryValidateTemplate(template, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("%1")]
    [InlineData("--url=%1")]
    [InlineData("\"%1\" %1")]
    [InlineData("--no-url")]
    [InlineData("\"%1\"\n--evil")]
    public void Templates_that_allow_argument_injection_are_rejected(string template)
    {
        Assert.False(ProtocolPolicy.TryValidateTemplate(template, out _, out var error));
        Assert.Equal(NativeErrorCodes.InvalidArgument, error.Code);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" \"%1\"", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\App\\app.exe \"%1\"", "C:\\App\\app.exe")]
    [InlineData("app.exe \"%1\"", null)]
    [InlineData("\"unterminated", null)]
    [InlineData("", null)]
    public void Command_executables_are_parsed(string command, string? expected)
    {
        if (!OperatingSystem.IsWindows() && expected is not null)
        {
            Assert.Skip("Windows paths are only fully qualified on Windows.");
        }

        Assert.Equal(expected, ProtocolPolicy.GetCommandExecutable(command));
    }

    [Fact]
    public void Linux_exec_lines_quote_arguments_and_use_the_url_field_code()
    {
        var exec = LinuxProtocolRegistrar.BuildExec("/opt/My App/app$1", "--open \"%1\" --label \"100% sure\"");

        // $ is escaped inside the quoted argument (\$), then the backslash is escaped again at the key-file level (\\$).
        Assert.True(exec.IsSuccess, exec.ToString());
        Assert.Equal("\"/opt/My App/app\\\\$1\" \"--open\" %u \"--label\" \"100%% sure\"", exec.Value);
        Assert.False(LinuxProtocolRegistrar.BuildExec("/app", "--url=\"%1\"x").IsSuccess);
        Assert.Equal("com.contoso_notes-myapp.desktop", LinuxProtocolRegistrar.GetDesktopFileName("com.contoso/notes", "myapp"));
    }

    [Fact]
    public async Task Invalid_schemes_fail_before_touching_the_system()
    {
        var service = CreateService(TestSupport.SdkOptions());

        Assert.Equal(NativeErrorCodes.InvalidArgument, (await service.RegisterAsync("https", cancellationToken: Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await service.UnregisterAsync("bad scheme", Ct)).Error!.Code);
        Assert.Equal(NativeErrorCodes.InvalidArgument, (await service.IsRegisteredAsync("", Ct)).Error!.Code);
    }

    [Fact]
    public async Task Windows_registration_round_trips_in_the_current_user_hive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Registry-based registration is Windows-only.");
            return;
        }

        var scheme = "bnstest-" + Guid.NewGuid().ToString("N")[..10];
        var executable = Environment.ProcessPath!;
        var service = CreateService(TestSupport.SdkOptions());
        try
        {
            var registered = await service.RegisterAsync(scheme, new ProtocolRegistrationOptions { ExecutablePath = executable, DisplayName = "Test" }, Ct);
            Assert.True(registered.IsSuccess, registered.ToString());

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme + @"\shell\open\command"))
            {
                Assert.Equal($"\"{executable}\" \"%1\"", key?.GetValue(null));
            }

            if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True((await service.IsRegisteredAsync(scheme, Ct)).Value);
            }

            // Another app's service may not remove or replace it.
            var other = CreateService(TestSupport.SdkOptions());
            Assert.Equal(NativeErrorCodes.Conflict, (await other.RegisterAsync(scheme, new ProtocolRegistrationOptions { ExecutablePath = Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\notepad.exe" }, Ct)).Error!.Code);

            Assert.True((await service.UnregisterAsync(scheme, Ct)).IsSuccess);
            Assert.False((await service.IsRegisteredAsync(scheme, Ct)).Value);
            Assert.True((await service.UnregisterAsync(scheme, Ct)).IsSuccess);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + scheme, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task MacOS_reports_unsupported()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS-only behavior.");
        }

        var status = await CreateService(TestSupport.SdkOptions()).GetStatusAsync(Ct);

        Assert.True(status.IsUnsupported);
    }

    private static DesktopProtocolRegistrationService CreateService(NativeSdkOptions sdk) =>
        new(Options.Create(sdk), Options.Create(new DesktopNativeOptions()), NullLogger<DesktopProtocolRegistrationService>.Instance);
}
