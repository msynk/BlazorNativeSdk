using System.Text.Json.Serialization;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>The JSON envelope carried by every IPC frame.</summary>
/// <remarks>
/// Wire format: a 4-byte little-endian length followed by this envelope as UTF-8 JSON. During the handshake
/// (<see cref="IpcKinds.Hello"/>, <see cref="IpcKinds.Auth"/>, <see cref="IpcKinds.Welcome"/>) <see cref="Type"/> carries the
/// protocol/authentication scheme, <see cref="Payload"/> a base64 nonce and <see cref="Mac"/> the base64 proof.
/// </remarks>
internal sealed class IpcEnvelope
{
    /// <summary>The frame kind, see <see cref="IpcKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The application message type, the handshake scheme, or an error code for <see cref="IpcKinds.Error"/>.</summary>
    public string? Type { get; set; }

    /// <summary>The application payload, a handshake nonce, or an error message.</summary>
    public string? Payload { get; set; }

    /// <summary>Correlates responses and errors with requests.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The per-direction sequence number (1, 2, 3, ... after the handshake; 0 during the handshake).</summary>
    public long Seq { get; set; }

    /// <summary>The base64 HMAC-SHA256 of the frame (authenticated sessions) or the handshake proof.</summary>
    public string? Mac { get; set; }
}

/// <summary>Envelope kinds.</summary>
internal static class IpcKinds
{
    public const string Hello = "hello";
    public const string Auth = "auth";
    public const string Welcome = "welcome";
    public const string Request = "request";
    public const string Response = "response";
    public const string OneWay = "oneway";
    public const string Push = "push";
    public const string Error = "error";
}

/// <summary>Source-generated JSON metadata for the desktop adapter (trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 8)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(SingleInstance.ForwardedLaunch))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
