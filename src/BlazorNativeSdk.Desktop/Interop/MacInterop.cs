using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlazorNativeSdk.Desktop.Interop;

/// <summary>Minimal Objective-C runtime bindings for the Foundation calls the desktop adapter needs on macOS.</summary>
/// <remarks>
/// <c>objc_msgSend</c> must be called through a prototype that matches the target method exactly (arm64 does not use
/// the variadic calling convention for it), so each message shape has its own import.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static partial class MacInterop
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";

    private static readonly Lazy<bool> s_foundationLoaded = new(() => NativeLibrary.TryLoad(FoundationPath, out _));

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjCLibrary)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendUtf8(nint receiver, nint selector, string value);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtrBool(nint receiver, nint selector, nint value, byte flag);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);

    /// <summary>
    /// Moves an item to the Trash with <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>, the API Finder
    /// uses: it picks the right trash for the volume and records "Put Back" information, without Automation prompts.
    /// </summary>
    /// <returns><see langword="null"/> on success, otherwise the localized error description.</returns>
    /// <exception cref="DllNotFoundException">The Objective-C runtime or Foundation is unavailable.</exception>
    internal static string? TrashItem(string path, bool isDirectory)
    {
        if (!s_foundationLoaded.Value)
        {
            throw new DllNotFoundException("Foundation.framework could not be loaded.");
        }

        var pool = objc_autoreleasePoolPush();
        try
        {
            var nsString = objc_getClass("NSString");
            var nsUrl = objc_getClass("NSURL");
            var nsFileManager = objc_getClass("NSFileManager");
            if (nsString == 0 || nsUrl == 0 || nsFileManager == 0)
            {
                throw new DllNotFoundException("Foundation classes are unavailable.");
            }

            var pathString = SendUtf8(nsString, sel_registerName("stringWithUTF8String:"), path);
            if (pathString == 0)
            {
                return "The path is not valid UTF-8.";
            }

            var url = SendPtrBool(nsUrl, sel_registerName("fileURLWithPath:isDirectory:"), pathString, isDirectory ? (byte)1 : (byte)0);
            var manager = SendPtr(nsFileManager, sel_registerName("defaultManager"));
            if (url == 0 || manager == 0)
            {
                return "Foundation could not create the file URL.";
            }

            if (SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, 0, out var error) != 0)
            {
                return null;
            }

            if (error == 0)
            {
                return "The item could not be moved to the Trash.";
            }

            var description = SendPtr(error, sel_registerName("localizedDescription"));
            var utf8 = description == 0 ? 0 : SendPtr(description, sel_registerName("UTF8String"));
            return utf8 == 0 ? "The item could not be moved to the Trash." : Marshal.PtrToStringUTF8(utf8);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }
}
