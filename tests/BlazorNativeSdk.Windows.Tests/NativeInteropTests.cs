using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Windows.Foundation;
using Windows.Security.Credentials.UI;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed unsafe class StructLayoutTests
{
    [Fact]
    public void Structs_match_the_windows_sdk_layout()
    {
        Assert.Equal(16, sizeof(RECT));
        Assert.Equal(104, sizeof(MONITORINFOEXW));
        Assert.Equal(44, sizeof(WINDOWPLACEMENT));
        Assert.Equal(840, sizeof(DISPLAY_DEVICEW));
        if (IntPtr.Size == 8)
        {
            // x64 and arm64 share the same alignment rules.
            Assert.Equal(976, sizeof(NOTIFYICONDATAW));
            Assert.Equal(80, sizeof(MENUITEMINFOW));
            Assert.Equal(48, sizeof(MSG));
            Assert.Equal(72, sizeof(GUITHREADINFO));
            Assert.Equal(32, sizeof(FLASHWINFO));
            Assert.Equal(80, sizeof(WNDCLASSEXW));
            Assert.Equal(16, (int)Marshal.OffsetOf<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.uID)));
            Assert.Equal(32, (int)Marshal.OffsetOf<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.hIcon)));
            Assert.Equal(952, (int)Marshal.OffsetOf<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.guidItem)));
            Assert.Equal(968, (int)Marshal.OffsetOf<NOTIFYICONDATAW>(nameof(NOTIFYICONDATAW.hBalloonIcon)));
            Assert.Equal(24, (int)Marshal.OffsetOf<MENUITEMINFOW>(nameof(MENUITEMINFOW.hSubMenu)));
            Assert.Equal(56, (int)Marshal.OffsetOf<MENUITEMINFOW>(nameof(MENUITEMINFOW.dwTypeData)));
        }
        else
        {
            Assert.Equal(956, sizeof(NOTIFYICONDATAW));
            Assert.Equal(48, sizeof(MENUITEMINFOW));
        }
    }

    [Fact]
    public void Hard_coded_iid_matches_the_cswinrt_generator() =>
        Assert.Equal(
            WinRT.GuidGenerator.CreateIID(typeof(IAsyncOperation<UserConsentVerificationResult>)),
            WinRTInterop.IID_IAsyncOperation_UserConsentVerificationResult);
}

public sealed class IconLoaderTests
{
    [Fact]
    public void Creates_icons_from_png_bytes()
    {
        var result = IconLoader.FromBytes(TestImages.Png(32, 32), 16, 16, NativeCapabilities.Tray);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        using var icon = result.Value;
        Assert.False(icon.IsInvalid);
    }

    [Fact]
    public void Creates_icons_from_ico_bytes_with_bitmap_and_png_entries()
    {
        var ico = TestImages.Ico((16, TestImages.Dib32(16)), (48, TestImages.Png(48, 48)));
        Assert.True(IconLoader.TrySelectIcoImage(ico, 16, out var offset, out var length));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(offset)));
        Assert.True(IconLoader.TrySelectIcoImage(ico, 32, out offset, out length));
        Assert.True(ico.AsSpan(offset, length).StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        foreach (var size in new[] { 16, 32 })
        {
            var result = IconLoader.FromBytes(ico, size, size, NativeCapabilities.Tray);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            result.Value.Dispose();
        }
    }

    [Fact]
    public void Rejects_unknown_or_truncated_data()
    {
        Assert.Equal(NativeErrorCodes.InvalidArgument, IconLoader.FromBytes([1, 2, 3, 4], 16, 16, NativeCapabilities.Tray).Error!.Code);
        var ico = TestImages.Ico((16, TestImages.Dib32(16)));
        Assert.False(IconLoader.TrySelectIcoImage(ico.AsSpan(0, 30), 16, out _, out _));
    }

    [Fact]
    public void Loads_the_application_icon_when_no_icon_is_given()
    {
        var result = IconLoader.Load(null, null, NativeCapabilities.Tray);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        using var icon = result.Value;
        Assert.False(icon.IsInvalid);
    }

    [Fact]
    public void Reports_missing_icon_files()
    {
        var result = IconLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico"), null, NativeCapabilities.Tray);
        Assert.Equal(NativeErrorCodes.NotFound, result.Error!.Code);
    }
}

public sealed class WindowControllerTests
{
    [Fact]
    public async Task Controls_bounds_topmost_title_and_placement_of_a_hidden_window()
    {
        using var thread = new MessageWindowThread();
        var hwnd = await thread.InvokeAsync(() => NativeMethods.CreateWindowExW(
            0, "STATIC", "bns-test", Win32.WS_OVERLAPPEDWINDOW, 100, 100, 400, 300, 0, 0, NativeMethods.GetModuleHandleW(null), 0));
        Assert.NotEqual(0, hwnd);
        try
        {
            var controller = new Win32WindowController(hwnd);
            Assert.True(controller.IsAlive);
            Assert.False(controller.IsVisible);
            Assert.Equal(NativeWindowState.Normal, controller.State);
            Assert.Equal("bns-test", controller.Title);

            Assert.True(controller.SetTitle("renamed").IsSuccess);
            Assert.Equal("renamed", controller.Title);

            var primary = (await new WindowsScreenService().GetDisplaysAsync(TestContext.Current.CancellationToken)).Value![0].WorkArea;
            var requested = new WindowBounds(primary.X + 50, primary.Y + 60, 320, 240);
            Assert.True(controller.SetBounds(requested).IsSuccess);
            AssertClose(requested, controller.Bounds);
            AssertClose(requested, controller.RestoredBounds);

            var placement = controller.ToPlacement();
            AssertClose(requested, placement.Bounds);
            Assert.Equal(NativeWindowState.Normal, placement.State);
            Assert.StartsWith(@"\\.\", placement.DisplayId, StringComparison.Ordinal);

            var moved = placement with { Bounds = requested with { X = requested.X + 10, Width = 300 } };
            Assert.True(controller.ApplyPlacement(moved, applyState: false).IsSuccess);
            Assert.False(controller.IsVisible);
            AssertClose(moved.Bounds, controller.RestoredBounds);

            Assert.True(controller.SetAlwaysOnTop(true).IsSuccess);
            Assert.True(controller.IsAlwaysOnTop);
            Assert.True(controller.SetAlwaysOnTop(false).IsSuccess);
            Assert.False(controller.IsAlwaysOnTop);

            Assert.False(controller.SetBounds(requested with { Width = 0 }).IsSuccess);
        }
        finally
        {
            await thread.InvokeAsync(() => NativeMethods.DestroyWindow(hwnd));
        }

        var gone = new Win32WindowController(hwnd);
        Assert.False(gone.IsAlive);
        Assert.Equal(NativeErrorCodes.NotFound, gone.SetState(NativeWindowState.Maximized).Error!.Code);
    }

    [Fact]
    public async Task Restores_to_normal_when_the_window_was_maximized_before_it_was_minimized()
    {
        using var thread = new MessageWindowThread();
        var hwnd = await thread.InvokeAsync(() => NativeMethods.CreateWindowExW(
            0, "STATIC", "bns-restore-test", Win32.WS_OVERLAPPEDWINDOW, 100, 100, 400, 300, 0, 0, NativeMethods.GetModuleHandleW(null), 0));
        Assert.NotEqual(0, hwnd);
        try
        {
            var controller = new Win32WindowController(hwnd);
            var normalBounds = await thread.InvokeAsync(() => controller.RestoredBounds);

            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Maximized))).IsSuccess);
            Assert.Equal(NativeWindowState.Maximized, controller.State);
            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Minimized))).IsSuccess);
            Assert.Equal(NativeWindowState.Minimized, controller.State);

            // A plain SW_RESTORE would bring this window back maximized.
            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Normal))).IsSuccess);
            Assert.Equal(NativeWindowState.Normal, controller.State);
            Assert.True(controller.IsVisible);
            AssertClose(normalBounds, controller.Bounds);

            // Maximized → Normal and Hidden → Normal still work.
            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Maximized))).IsSuccess);
            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Normal))).IsSuccess);
            Assert.Equal(NativeWindowState.Normal, controller.State);
            Assert.True((await thread.InvokeAsync(() => controller.SetVisible(false))).IsSuccess);
            Assert.True((await thread.InvokeAsync(() => controller.SetState(NativeWindowState.Normal))).IsSuccess);
            Assert.True(controller.IsVisible);
            Assert.Equal(NativeWindowState.Normal, controller.State);
        }
        finally
        {
            await thread.InvokeAsync(() => NativeMethods.DestroyWindow(hwnd));
        }
    }

    private static void AssertClose(WindowBounds expected, WindowBounds actual)
    {
        Assert.InRange(actual.X, expected.X - 1, expected.X + 1);
        Assert.InRange(actual.Y, expected.Y - 1, expected.Y + 1);
        Assert.InRange(actual.Width, expected.Width - 1, expected.Width + 1);
        Assert.InRange(actual.Height, expected.Height - 1, expected.Height + 1);
    }
}

/// <summary>Builds small PNG/ICO images without external dependencies.</summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        var raw = new byte[height * ((width * 4) + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * ((width * 4) + 1);
            for (var x = 0; x < width; x++)
            {
                var pixel = row + 1 + (x * 4);
                raw[pixel] = 0x20;
                raw[pixel + 1] = 0x80;
                raw[pixel + 2] = 0xE0;
                raw[pixel + 3] = 0xFF;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static byte[] Dib32(int size)
    {
        var maskStride = ((size + 31) / 32) * 4;
        var data = new byte[40 + (size * size * 4) + (maskStride * size)];
        BinaryPrimitives.WriteInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), size);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), size * 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(14), 32);
        for (var i = 40; i < 40 + (size * size * 4); i += 4)
        {
            data[i] = 0xE0;
            data[i + 1] = 0x80;
            data[i + 2] = 0x20;
            data[i + 3] = 0xFF;
        }

        return data;
    }

    public static byte[] Ico(params (int Size, byte[] Image)[] images)
    {
        var headerSize = 6 + (16 * images.Length);
        var result = new byte[headerSize + images.Sum(i => i.Image.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), (ushort)images.Length);
        var offset = headerSize;
        for (var i = 0; i < images.Length; i++)
        {
            var entry = result.AsSpan(6 + (i * 16), 16);
            entry[0] = (byte)(images[i].Size >= 256 ? 0 : images[i].Size);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], images[i].Image.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            images[i].Image.CopyTo(result, offset);
            offset += images[i].Image.Length;
        }

        return result;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32([.. typeBytes, .. data]));
        output.Write(buffer);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
