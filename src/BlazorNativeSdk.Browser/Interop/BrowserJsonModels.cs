using System.Text.Json.Serialization;

namespace BlazorNativeSdk.Browser.Interop;

// Data transfer objects exchanged with blazor-native-sdk.js. Requests are serialized to JSON strings with
// BrowserJsonContext and parsed with JSON.parse in JavaScript; results come back as JsonElement and are deserialized with
// the same context, so no reflection-based serialization is involved.

/// <summary>An error reported by the JavaScript module (usually a <c>DOMException</c>).</summary>
internal sealed record BrowserJsError
{
    public required string Name { get; init; }

    public string? Message { get; init; }

    /// <summary>Whether the page had transient user activation when the call started; <see langword="null"/> when the browser does not report it.</summary>
    public bool? UserActivation { get; init; }

    /// <summary>The permission state observed when the error happened (<c>granted</c>, <c>denied</c>, <c>prompt</c>, <c>default</c>).</summary>
    public string? PermissionState { get; init; }
}

internal sealed record SessionOptionsDto
{
    public required string ProtocolParameter { get; init; }

    public required string NotificationParameter { get; init; }

    public bool UseServiceWorkerNotifications { get; init; }

    public required OverlayTextDto Text { get; init; }
}

internal sealed record OverlayTextDto
{
    public required string CameraTitle { get; init; }

    public required string ScannerTitle { get; init; }

    public required string ScannerPrompt { get; init; }

    public required string Capture { get; init; }

    public required string StartRecording { get; init; }

    public required string StopRecording { get; init; }

    public required string Cancel { get; init; }

    public required string SwitchCamera { get; init; }
}

internal sealed record CameraRequestDto
{
    public string? Facing { get; init; }

    public int? MaxDimension { get; init; }

    public double Quality { get; init; } = 0.9;

    public string? Title { get; init; }

    public bool Video { get; init; }

    public double? MaxDurationMs { get; init; }

    /// <summary>The <c>accept</c> attribute for the file-input fallback.</summary>
    public required string Accept { get; init; }
}

internal sealed record FileTypeDto
{
    public required string Description { get; init; }

    public required IReadOnlyList<string> MimeTypes { get; init; }

    public required IReadOnlyList<string> Extensions { get; init; }
}

internal sealed record FilePickRequestDto
{
    public bool Multiple { get; init; }

    public required IReadOnlyList<FileTypeDto> Types { get; init; }

    public required string Accept { get; init; }

    public bool UseFileSystemAccess { get; init; }
}

internal sealed record FileSaveRequestDto
{
    public required string SuggestedName { get; init; }

    public required string ContentType { get; init; }

    public required IReadOnlyList<FileTypeDto> Types { get; init; }

    public bool UseFileSystemAccess { get; init; }
}

internal sealed record BarcodeScanRequestDto
{
    public required IReadOnlyList<string> Formats { get; init; }

    public string? Facing { get; init; }

    public double? TimeoutMs { get; init; }

    public string? Prompt { get; init; }
}

internal sealed record BarcodeDetectRequestDto
{
    public required IReadOnlyList<string> Formats { get; init; }

    public string? ContentType { get; init; }
}

internal sealed record ShareFileDto
{
    /// <summary><c>blob</c> when the argument is a JavaScript Blob reference, <c>stream</c> for a .NET stream reference.</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? Type { get; init; }
}

internal sealed record ShareRequestDto
{
    public string? Title { get; init; }

    public string? Text { get; init; }

    public string? Url { get; init; }

    public required IReadOnlyList<ShareFileDto> Files { get; init; }
}

internal sealed record NotificationActionDto
{
    public required string Action { get; init; }

    public required string Title { get; init; }
}

internal sealed record NotificationRequestDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public string? Route { get; init; }

    public required IReadOnlyDictionary<string, string> Data { get; init; }

    public required IReadOnlyList<NotificationActionDto> Actions { get; init; }

    public bool Silent { get; init; }

    public bool RequireInteraction { get; init; }

    public int? Badge { get; init; }
}

internal sealed record BrowserFileDto
{
    public required string Handle { get; init; }

    public required string Name { get; init; }

    public string? Type { get; init; }

    public long? Size { get; init; }

    /// <summary>Milliseconds since the Unix epoch.</summary>
    public double? LastModified { get; init; }
}

internal sealed record BarcodeDto
{
    public string? RawValue { get; init; }

    public required string Format { get; init; }

    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }
}

internal sealed record SavedFileDto
{
    /// <summary><c>dialog</c> or <c>download</c>.</summary>
    public required string Method { get; init; }

    public required string Name { get; init; }
}

internal sealed record ConnectivityDto
{
    public bool OnLine { get; init; }

    public string? Type { get; init; }

    public bool? SaveData { get; init; }
}

internal sealed record NotificationClickDto
{
    public string? Id { get; init; }

    public string? Action { get; init; }

    public string? Route { get; init; }

    public Dictionary<string, string>? Data { get; init; }

    public string? Reply { get; init; }
}

internal sealed record LaunchInfoDto
{
    /// <summary>The URL carried by the protocol-handler query parameter.</summary>
    public string? Link { get; init; }

    /// <summary>A notification tap that opened this page through the service worker.</summary>
    public NotificationClickDto? NotificationClick { get; init; }
}

/// <summary>Feature detection results. Only the fields relevant to the probed section are set.</summary>
internal sealed record BrowserProbeDto
{
    public bool SecureContext { get; init; }

    public bool? UserActivation { get; init; }

    public bool UserMedia { get; init; }

    public bool MediaRecorder { get; init; }

    public int? VideoInputs { get; init; }

    public string? CameraPermission { get; init; }

    public bool BarcodeDetector { get; init; }

    public IReadOnlyList<string>? BarcodeFormats { get; init; }

    public bool OpenFilePicker { get; init; }

    public bool SaveFilePicker { get; init; }

    public bool Share { get; init; }

    public bool CanShareFiles { get; init; }

    public bool ClipboardRead { get; init; }

    public bool ClipboardWrite { get; init; }

    public bool ExecCommandCopy { get; init; }

    public bool PermissionsApi { get; init; }

    public bool Notification { get; init; }

    public string? NotificationPermission { get; init; }

    public bool ServiceWorkerRegistration { get; init; }

    public int? MaxNotificationActions { get; init; }

    public bool Badging { get; init; }

    public bool IndexedDb { get; init; }

    public bool SubtleCrypto { get; init; }

    public bool ProtocolHandler { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(BrowserJsError))]
[JsonSerializable(typeof(SessionOptionsDto))]
[JsonSerializable(typeof(CameraRequestDto))]
[JsonSerializable(typeof(FilePickRequestDto))]
[JsonSerializable(typeof(FileSaveRequestDto))]
[JsonSerializable(typeof(BarcodeScanRequestDto))]
[JsonSerializable(typeof(BarcodeDetectRequestDto))]
[JsonSerializable(typeof(ShareRequestDto))]
[JsonSerializable(typeof(NotificationRequestDto))]
[JsonSerializable(typeof(BrowserFileDto[]))]
[JsonSerializable(typeof(BarcodeDto))]
[JsonSerializable(typeof(BarcodeDto[]))]
[JsonSerializable(typeof(SavedFileDto))]
[JsonSerializable(typeof(ConnectivityDto))]
[JsonSerializable(typeof(NotificationClickDto))]
[JsonSerializable(typeof(LaunchInfoDto))]
[JsonSerializable(typeof(BrowserProbeDto))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;
