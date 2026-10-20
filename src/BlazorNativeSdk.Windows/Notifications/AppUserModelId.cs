using System.Runtime.InteropServices;
using System.Text;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Win32;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Application User Model ID helpers for unpackaged apps.</summary>
internal static class AppUserModelId
{
    private const int MaxLength = 128;
    private const string RegistryRoot = @"Software\Classes\AppUserModelId\";

    /// <summary>Makes an id usable as an AUMID: no whitespace or backslashes, at most 128 characters.</summary>
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var builder = new StringBuilder(Math.Min(value.Length, MaxLength));
        foreach (var c in value.Trim())
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            builder.Append(char.IsWhiteSpace(c) || c == '\\' || char.IsControl(c) ? '.' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Registers the AUMID under <c>HKCU\Software\Classes\AppUserModelId\{aumid}</c> with the display name and icon shown on
    /// toasts. Values are only written when they change.
    /// </summary>
    public static void Register(string aumid, string displayName, string? iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryRoot + aumid, writable: true);
        SetIfChanged(key, "DisplayName", displayName);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var fullPath = Path.GetFullPath(iconPath);
            if (File.Exists(fullPath))
            {
                SetIfChanged(key, "IconUri", fullPath);
            }
        }
    }

    /// <summary>Sets the process-wide explicit AUMID (also used by the taskbar to group the app's windows).</summary>
    public static void ApplyToProcess(string aumid) =>
        Marshal.ThrowExceptionForHR(NativeMethods.SetCurrentProcessExplicitAppUserModelID(aumid));

    private static void SetIfChanged(RegistryKey key, string name, string value)
    {
        if (!string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal))
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }
}
