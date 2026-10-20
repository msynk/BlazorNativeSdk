using System.Runtime.Versioning;
using BlazorNativeSdk.Desktop.Interop;
using Microsoft.Win32;

namespace BlazorNativeSdk.Desktop.Protocols;

/// <summary>Per-user URL protocol registration under <c>HKCU\Software\Classes\{scheme}</c>.</summary>
/// <remarks>
/// <code>
/// HKCU\Software\Classes\{scheme}
///     (Default)               = "URL:{display name}"
///     URL Protocol            = ""
///     BlazorNativeSdk.AppId   = "{app id}"          (ownership marker)
///     DefaultIcon\(Default)   = "\"{exe}\",0"
///     shell\open\command\(Default) = "\"{exe}\" \"%1\""
/// </code>
/// HKCU registrations need no elevation and take precedence over machine-wide ones for the user. Packaged (MSIX) apps get
/// a virtualized registry view, so they must declare protocols in <c>Package.appxmanifest</c> instead.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsProtocolRegistrar
{
    private const string ClassesPath = @"Software\Classes\";
    private const string OwnerValueName = "BlazorNativeSdk.AppId";
    private static readonly CapabilityId s_capability = NativeCapabilities.ProtocolRegistration;

    public static NativeError? GetUnsupportedReason() =>
        WindowsInterop.IsPackagedProcess()
            ? NativeError.Unsupported(s_capability,
                "The app runs with MSIX package identity; declare the protocol in Package.appxmanifest (uap:Protocol) instead of registering it at runtime.",
                NativeErrorCodes.MissingDeclaration)
            : null;

    public static NativeResult Register(string scheme, string executable, string template, string displayName, string appId, bool overwriteForeign)
    {
        var keyPath = ClassesPath + scheme;
        using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            if (existing is not null && !IsOwned(existing, appId, executable) && !overwriteForeign)
            {
                return NativeError.Failed(s_capability, NativeErrorCodes.Conflict,
                    $"The '{scheme}' scheme is registered to another application ({ReadCommand(existing) ?? "unknown"}). " +
                    "Set DesktopNativeOptions.OverwriteForeignProtocolRegistrations to replace it.");
            }
        }

        // Start from a clean key so stale verbs (ddeexec, other shell verbs) from earlier registrations cannot interfere.
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
        {
            key.SetValue(null, "URL:" + displayName);
            key.SetValue("URL Protocol", string.Empty);
            key.SetValue(OwnerValueName, appId);
            using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
            {
                icon.SetValue(null, $"\"{executable}\",0");
            }

            using var command = key.CreateSubKey(@"shell\open\command", writable: true);
            command.SetValue(null, $"\"{executable}\" {template}");
        }

        NotifyShell();
        return NativeResult.Ok;
    }

    public static NativeResult Unregister(string scheme, string executable, string appId)
    {
        var keyPath = ClassesPath + scheme;
        using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            if (existing is null)
            {
                return NativeResult.Ok;
            }

            if (!IsOwned(existing, appId, executable))
            {
                return NativeError.Failed(s_capability, NativeErrorCodes.Conflict,
                    $"The '{scheme}' scheme is registered to another application and was left unchanged.");
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        NotifyShell();
        return NativeResult.Ok;
    }

    public static bool IsRegistered(string scheme, string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassesPath + scheme);
        return key?.GetValue("URL Protocol") is not null &&
               string.Equals(ProtocolPolicy.GetCommandExecutable(ReadCommand(key)), executable, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwned(RegistryKey key, string appId, string executable) =>
        string.Equals(key.GetValue(OwnerValueName) as string, appId, StringComparison.Ordinal) ||
        string.Equals(ProtocolPolicy.GetCommandExecutable(ReadCommand(key)), executable, StringComparison.OrdinalIgnoreCase);

    private static string? ReadCommand(RegistryKey key)
    {
        using var command = key.OpenSubKey(@"shell\open\command");
        return command?.GetValue(null) as string;
    }

    private static void NotifyShell() =>
        WindowsInterop.SHChangeNotify(WindowsInterop.ShcneAssocChanged, WindowsInterop.ShcnfIdList, 0, 0);
}
