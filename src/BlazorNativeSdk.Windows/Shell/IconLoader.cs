using System.Buffers.Binary;
using BlazorNativeSdk.WindowsPlatform.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Creates HICONs for notification-area icons from .ico files, ICO/PNG bytes or the application's own icon.</summary>
internal static unsafe class IconLoader
{
    private const int MaxIconBytes = 4 * 1024 * 1024;
    private const uint IconResourceVersion = 0x00030000;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The small-icon size (tray icons) at the system DPI.</summary>
    public static (int Width, int Height) SmallIconSize
    {
        get
        {
            var dpi = NativeMethods.GetDpiForSystem();
            var cx = NativeMethods.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
            var cy = NativeMethods.GetSystemMetricsForDpi(Win32.SM_CYSMICON, dpi);
            return (cx > 0 ? cx : 16, cy > 0 ? cy : 16);
        }
    }

    /// <summary>Loads an icon from a path, bytes, or the app icon when both are absent.</summary>
    public static NativeResult<SafeIconHandle> Load(string? path, ReadOnlyMemory<byte>? data, CapabilityId capability)
    {
        var (cx, cy) = SmallIconSize;
        if (data is { Length: > 0 } bytes)
        {
            return FromBytes(bytes.Span, cx, cy, capability);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            return FromFile(path, cx, cy, capability);
        }

        return FromApplication(cx, cy);
    }

    /// <summary>Creates an icon from ICO or PNG bytes, choosing the ICO entry that best matches the requested size.</summary>
    public static NativeResult<SafeIconHandle> FromBytes(ReadOnlySpan<byte> data, int cx, int cy, CapabilityId capability)
    {
        if (data.Length > MaxIconBytes)
        {
            return NativeError.Failed(capability, NativeErrorCodes.TooLarge, $"Icon data exceeds {MaxIconBytes} bytes.");
        }

        ReadOnlySpan<byte> image;
        if (data.StartsWith(PngSignature))
        {
            image = data;
        }
        else if (TrySelectIcoImage(data, cx, out var offset, out var length))
        {
            image = data.Slice(offset, length);
        }
        else
        {
            return NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "Icon data must be an ICO file or a PNG image.");
        }

        fixed (byte* bits = image)
        {
            var handle = NativeMethods.CreateIconFromResourceEx(bits, (uint)image.Length, true, IconResourceVersion, cx, cy, Win32.LR_DEFAULTCOLOR);
            return handle != 0
                ? new SafeIconHandle(handle, ownsHandle: true)
                : NativeError.Failed(capability, NativeErrorCodes.InvalidArgument, "The icon image could not be decoded.");
        }
    }

    /// <summary>
    /// Picks the ICONDIRENTRY to use: the smallest image at least as large as <paramref name="desiredSize"/> with the highest
    /// color depth, or the largest image when none is large enough.
    /// </summary>
    internal static bool TrySelectIcoImage(ReadOnlySpan<byte> data, int desiredSize, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 1)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (count == 0 || data.Length < 6 + (count * 16))
        {
            return false;
        }

        var bestScore = long.MinValue;
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(6 + (i * 16), 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var bytesInRes = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (bytesInRes == 0 || imageOffset >= (uint)data.Length || bytesInRes > (uint)data.Length - imageOffset)
            {
                continue;
            }

            // Larger-or-equal images beat smaller ones; among them prefer the closest size, then the deepest color.
            long score = width >= desiredSize ? 1_000_000 - ((width - desiredSize) * 1000) : width * 1000;
            score += bitCount;
            if (score > bestScore)
            {
                bestScore = score;
                offset = (int)imageOffset;
                length = (int)bytesInRes;
            }
        }

        return length > 0;
    }

    private static NativeResult<SafeIconHandle> FromFile(string path, int cx, int cy, CapabilityId capability)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return NativeError.Failed(capability, NativeErrorCodes.NotFound, $"Icon file '{path}' does not exist.");
            }

            if (string.Equals(Path.GetExtension(fullPath), ".ico", StringComparison.OrdinalIgnoreCase))
            {
                var handle = NativeMethods.LoadImageW(0, fullPath, Win32.IMAGE_ICON, cx, cy, Win32.LR_LOADFROMFILE);
                if (handle != 0)
                {
                    return new SafeIconHandle(handle, ownsHandle: true);
                }
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaxIconBytes)
            {
                return NativeError.Failed(capability, NativeErrorCodes.TooLarge, $"Icon file '{path}' exceeds {MaxIconBytes} bytes.");
            }

            return FromBytes(File.ReadAllBytes(fullPath), cx, cy, capability);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NativeError.FromException(capability, ex);
        }
    }

    private static SafeIconHandle FromApplication(int cx, int cy)
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            nint small = 0;
            if (NativeMethods.SHDefExtractIconW(exe, 0, 0, null, &small, (uint)((cy << 16) | (cx & 0xFFFF))) == 0 && small != 0)
            {
                return new SafeIconHandle(small, ownsHandle: true);
            }

            var buffer = stackalloc char[260 + 1];
            var span = new Span<char>(buffer, 260);
            if (exe.Length < 260 && exe.AsSpan().TryCopyTo(span))
            {
                buffer[exe.Length] = '\0';
                ushort index = 0;
                var associated = NativeMethods.ExtractAssociatedIconW(NativeMethods.GetModuleHandleW(null), buffer, &index);
                if (associated != 0)
                {
                    return new SafeIconHandle(associated, ownsHandle: true);
                }
            }
        }

        // Shared system icon: must not be destroyed.
        return new SafeIconHandle(NativeMethods.LoadIconW(0, Win32.IDI_APPLICATION), ownsHandle: false);
    }
}
