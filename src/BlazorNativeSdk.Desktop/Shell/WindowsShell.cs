using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlazorNativeSdk.Desktop.Interop;

namespace BlazorNativeSdk.Desktop.Shell;

/// <summary>Windows shell operations. Must run on an STA thread (see <see cref="Internal.StaThread"/>).</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShell
{
    private static readonly CapabilityId s_capability = NativeCapabilities.DesktopFiles;

    /// <summary>Selects the item in Explorer with <c>SHOpenFolderAndSelectItems</c>, falling back to <c>explorer.exe /select,</c>.</summary>
    public static NativeResult Reveal(string fullPath)
    {
        var pidl = WindowsInterop.ILCreateFromPath(fullPath);
        if (pidl != 0)
        {
            try
            {
                // cidl = 0: the PIDL identifies the item itself, which is selected inside its parent folder.
                if (WindowsInterop.SHOpenFolderAndSelectItems(pidl, 0, 0, 0) >= 0)
                {
                    return NativeResult.Ok;
                }
            }
            finally
            {
                WindowsInterop.ILFree(pidl);
            }
        }

        // Fallback. The path was validated (exists, fully qualified, no quote characters), so quoting it is unambiguous.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(explorer)
            {
                Arguments = "/select,\"" + fullPath + "\"",
                UseShellExecute = false,
            });
            return NativeResult.Ok;
        }
        catch (Win32Exception ex)
        {
            return NativeError.Failed(s_capability, NativeErrorCodes.PlatformError, $"Explorer could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>Opens a file, folder or URI with its default handler (<c>ShellExecuteEx</c>).</summary>
    public static NativeResult ShellExecute(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return NativeResult.Ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WindowsInterop.ErrorNoAssociation)
        {
            return NativeError.Unavailable(s_capability, DesktopErrorCodes.NoAssociation, "No application is associated with this item.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WindowsInterop.ErrorCancelled)
        {
            return NativeError.Cancelled(s_capability, "The user cancelled the operation.");
        }
        catch (Win32Exception ex)
        {
            return NativeError.Failed(s_capability, NativeErrorCodes.PlatformError, ex.Message, ex);
        }
    }

    /// <summary>
    /// Moves an item to the Recycle Bin with <c>SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO)</c>. <c>FOF_WANTNUKEWARNING</c>
    /// makes Windows ask before permanently deleting an item that cannot be recycled (network shares, oversized items)
    /// instead of silently destroying it; declining is reported as <see cref="NativeResultStatus.Cancelled"/>.
    /// </summary>
    public static NativeResult MoveToRecycleBin(string fullPath)
    {
        // pFrom is a list of NUL-terminated strings ending with an extra NUL: StringToHGlobalUni appends the final one.
        var from = Marshal.StringToHGlobalUni(fullPath + "\0");
        try
        {
            const ushort Flags = WindowsInterop.FofAllowUndo | WindowsInterop.FofNoConfirmation | WindowsInterop.FofSilent |
                                 WindowsInterop.FofNoErrorUi | WindowsInterop.FofWantNukeWarning;
            int result;
            bool aborted;
            if (IntPtr.Size == 8)
            {
                var operation = new WindowsInterop.ShFileOpStruct64 { Func = WindowsInterop.FoDelete, From = from, Flags = Flags };
                result = WindowsInterop.SHFileOperation64(ref operation);
                aborted = operation.AnyOperationsAborted != 0;
            }
            else
            {
                var operation = new WindowsInterop.ShFileOpStruct32 { Func = WindowsInterop.FoDelete, From = from, Flags = Flags };
                result = WindowsInterop.SHFileOperation32(ref operation);
                aborted = operation.AnyOperationsAborted != 0;
            }

            if (aborted || result == WindowsInterop.ErrorCancelled)
            {
                return NativeError.Cancelled(s_capability, "The item was not deleted (the user declined, or it cannot be recycled).");
            }

            if (result != 0)
            {
                return new NativeError(NativeResultStatus.Failed, NativeErrorCodes.PlatformError,
                    $"SHFileOperation failed with code 0x{result:X}.")
                {
                    Capability = s_capability,
                    Details = new Dictionary<string, string> { ["shfileoperation"] = result.ToString("X", CultureInfo.InvariantCulture) },
                };
            }

            return NativeResult.Ok;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
