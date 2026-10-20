using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Browser.Interop;

/// <summary>Maps <see cref="BarcodeFormats"/> to and from the format names of the Shape Detection API.</summary>
internal static class BarcodeFormatMapper
{
    private static readonly (BarcodeFormats Format, string Name)[] s_map =
    [
        (BarcodeFormats.QrCode, "qr_code"),
        (BarcodeFormats.Aztec, "aztec"),
        (BarcodeFormats.DataMatrix, "data_matrix"),
        (BarcodeFormats.Pdf417, "pdf417"),
        (BarcodeFormats.Code128, "code_128"),
        (BarcodeFormats.Code39, "code_39"),
        (BarcodeFormats.Code93, "code_93"),
        (BarcodeFormats.Codabar, "codabar"),
        (BarcodeFormats.Ean13, "ean_13"),
        (BarcodeFormats.Ean8, "ean_8"),
        (BarcodeFormats.Itf, "itf"),
        (BarcodeFormats.UpcA, "upc_a"),
        (BarcodeFormats.UpcE, "upc_e"),
    ];

    /// <summary>Returns the <c>BarcodeDetector</c> names for the requested formats, in a stable order.</summary>
    public static IReadOnlyList<string> ToDetectorNames(BarcodeFormats formats)
    {
        var names = new List<string>(s_map.Length);
        foreach (var (format, name) in s_map)
        {
            if ((formats & format) == format)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Returns the detector names for the requested formats that the browser reports in <c>getSupportedFormats()</c>.
    /// </summary>
    public static IReadOnlyList<string> Intersect(BarcodeFormats formats, IEnumerable<string>? supported)
    {
        var requested = ToDetectorNames(formats);
        if (supported is null)
        {
            return requested;
        }

        var set = new HashSet<string>(supported, StringComparer.Ordinal);
        return [.. requested.Where(set.Contains)];
    }

    /// <summary>Maps a detector format name; unknown names (for example <c>unknown</c>) map to <see cref="BarcodeFormats.None"/>.</summary>
    public static BarcodeFormats FromDetectorName(string? name)
    {
        foreach (var (format, candidate) in s_map)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return format;
            }
        }

        return BarcodeFormats.None;
    }

    /// <summary>Converts the formats reported by the browser to flags.</summary>
    public static BarcodeFormats FromDetectorNames(IEnumerable<string>? names)
    {
        var result = BarcodeFormats.None;
        foreach (var name in names ?? [])
        {
            result |= FromDetectorName(name);
        }

        return result;
    }

    /// <summary>Converts a detected barcode.</summary>
    public static Barcode? ToBarcode(BarcodeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var format = FromDetectorName(dto.Format);
        if (format == BarcodeFormats.None)
        {
            return null;
        }

        return new Barcode
        {
            Format = format,
            RawValue = dto.RawValue,
            Bounds = dto is { X: { } x, Y: { } y, Width: { } w, Height: { } h } ? new BarcodeBounds(x, y, w, h) : null,
        };
    }
}

/// <summary>Maps camera and picker options to browser parameters.</summary>
internal static class BrowserMediaMapper
{
    /// <summary>The <c>facingMode</c> constraint for a camera preference.</summary>
    public static string? ToFacingMode(CameraFacing facing) => facing switch
    {
        CameraFacing.Front => "user",
        CameraFacing.Rear => "environment",
        _ => null,
    };

    /// <summary>The <c>accept</c> attribute for media kinds.</summary>
    public static string ToAccept(MediaKinds kinds) => kinds switch
    {
        MediaKinds.Image => "image/*",
        MediaKinds.Video => "video/*",
        _ => "image/*,video/*",
    };

    /// <summary>Clamps a JPEG quality from 1-100 to the 0-1 range used by <c>canvas.toBlob</c>.</summary>
    public static double ToCanvasQuality(int quality) => Math.Clamp(quality, 1, 100) / 100.0;

    /// <summary>Converts file type filters to the DTO used by the module.</summary>
    public static IReadOnlyList<FileTypeDto> ToFileTypes(IReadOnlyList<FileTypeFilter>? filters) =>
        filters is null
            ? []
            : [.. filters.Select(f => new FileTypeDto { Description = f.Description, MimeTypes = f.MimeTypes, Extensions = f.Extensions })];

    /// <summary>Builds an <c>accept</c> attribute that lists every MIME type and extension of the filters.</summary>
    public static string ToAccept(IReadOnlyList<FileTypeFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            foreach (var value in filter.MimeTypes.Concat(filter.Extensions))
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value.Trim()))
                {
                    parts.Add(value.Trim());
                }
            }
        }

        return string.Join(',', parts);
    }

    /// <summary>Converts a JavaScript timestamp (milliseconds since the Unix epoch).</summary>
    public static DateTimeOffset? FromJsTimestamp(double? milliseconds) =>
        milliseconds is { } ms && double.IsFinite(ms) && ms > 0 && ms < 8.64e15
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;
}

/// <summary>Maps the Network Information API.</summary>
internal static class ConnectivityMapper
{
    /// <summary>Creates a snapshot from <c>navigator.onLine</c> and <c>navigator.connection</c>.</summary>
    public static ConnectivitySnapshot ToSnapshot(ConnectivityDto dto, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var type = ToConnectionType(dto.Type);
        return new ConnectivitySnapshot
        {
            Access = dto.OnLine ? NetworkAccess.Internet : NetworkAccess.None,
            ConnectionTypes = dto.OnLine && type is { } t ? [t] : [],
            IsMetered = dto.OnLine && type == ConnectionType.Cellular ? true : null,
            IsDataSaverEnabled = dto.SaveData,
            ObservedAt = observedAt,
        };
    }

    /// <summary>Maps <c>NetworkInformation.type</c>; <see langword="null"/> when unknown or not reported.</summary>
    public static ConnectionType? ToConnectionType(string? type) => type switch
    {
        "wifi" => ConnectionType.WiFi,
        "cellular" => ConnectionType.Cellular,
        "ethernet" => ConnectionType.Ethernet,
        "bluetooth" => ConnectionType.Bluetooth,
        "wimax" or "mixed" or "other" => ConnectionType.Other,
        _ => null,
    };
}

/// <summary>Maps page lifecycle states reported by the module.</summary>
internal static class LifecycleStateMapper
{
    /// <summary>Maps <c>active</c>, <c>inactive</c>, <c>background</c> and <c>terminating</c>.</summary>
    public static AppLifecycleState? ToState(string? state) => state switch
    {
        "active" => AppLifecycleState.Active,
        "inactive" => AppLifecycleState.Inactive,
        "background" => AppLifecycleState.Background,
        "terminating" => AppLifecycleState.Terminating,
        _ => null,
    };
}
