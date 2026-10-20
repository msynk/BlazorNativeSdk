namespace BlazorNativeSdk;

/// <summary>Runtime availability of a capability in the current host.</summary>
public enum CapabilityAvailability
{
    /// <summary>Availability could not be determined without attempting the operation.</summary>
    Unknown = 0,

    /// <summary>The capability is ready to use.</summary>
    Available = 1,

    /// <summary>The capability is supported, but using it will prompt the user for permission.</summary>
    RequiresPermission = 2,

    /// <summary>The capability is supported, but the user or system has denied the required permission.</summary>
    PermissionDenied = 3,

    /// <summary>The capability is supported by the platform but cannot be used right now (missing hardware, not configured, insecure context, ...).</summary>
    Unavailable = 4,

    /// <summary>The capability is not supported by the platform or host, or no adapter is registered.</summary>
    Unsupported = 5,
}

/// <summary>
/// A snapshot of a capability's availability, including the optional features the current adapter supports.
/// Use it to adapt the UI up front instead of discovering limitations through failures.
/// </summary>
public sealed record CapabilityStatus
{
    private static readonly IReadOnlySet<string> s_noFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The capability this status describes.</summary>
    public required CapabilityId Capability { get; init; }

    /// <summary>The availability.</summary>
    public required CapabilityAvailability Availability { get; init; }

    /// <summary>A machine-readable reason code when the capability is not fully available (see <see cref="NativeErrorCodes"/>).</summary>
    public string? ReasonCode { get; init; }

    /// <summary>A developer-facing explanation when the capability is not fully available.</summary>
    public string? Reason { get; init; }

    /// <summary>Optional features supported by the adapter (for example <c>video</c> for the camera).</summary>
    public IReadOnlySet<string> Features { get; init; } = s_noFeatures;

    /// <summary>The adapter that provides the capability (for example <c>browser</c>, <c>android</c>, <c>windows</c>).</summary>
    public string? Adapter { get; init; }

    /// <summary><see langword="true"/> when the capability can be used without prompting.</summary>
    public bool IsAvailable => Availability == CapabilityAvailability.Available;

    /// <summary><see langword="true"/> when it makes sense to attempt the operation (it is available or may prompt for permission).</summary>
    public bool IsUsable => Availability is CapabilityAvailability.Available or CapabilityAvailability.RequiresPermission or CapabilityAvailability.Unknown;

    /// <summary><see langword="true"/> when the platform or host does not support the capability at all.</summary>
    public bool IsUnsupported => Availability == CapabilityAvailability.Unsupported;

    /// <summary>Returns whether the adapter reports support for an optional feature.</summary>
    public bool Supports(string feature) => Features.Contains(feature);

    /// <summary>Creates an <see cref="CapabilityAvailability.Available"/> status.</summary>
    public static CapabilityStatus Available(CapabilityId capability, string? adapter = null, params IEnumerable<string> features) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.Available,
        Adapter = adapter,
        Features = CreateFeatureSet(features),
    };

    /// <summary>Creates a <see cref="CapabilityAvailability.RequiresPermission"/> status.</summary>
    public static CapabilityStatus RequiresPermission(CapabilityId capability, string? adapter = null, params IEnumerable<string> features) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.RequiresPermission,
        Adapter = adapter,
        Features = CreateFeatureSet(features),
    };

    /// <summary>Creates a <see cref="CapabilityAvailability.PermissionDenied"/> status.</summary>
    public static CapabilityStatus PermissionDenied(CapabilityId capability, string? reason = null, string? adapter = null) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.PermissionDenied,
        ReasonCode = NativeErrorCodes.PermissionDenied,
        Reason = reason ?? "The required permission has been denied.",
        Adapter = adapter,
    };

    /// <summary>Creates an <see cref="CapabilityAvailability.Unavailable"/> status.</summary>
    public static CapabilityStatus Unavailable(CapabilityId capability, string reasonCode, string reason, string? adapter = null) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.Unavailable,
        ReasonCode = reasonCode,
        Reason = reason,
        Adapter = adapter,
    };

    /// <summary>Creates an <see cref="CapabilityAvailability.Unsupported"/> status.</summary>
    public static CapabilityStatus Unsupported(CapabilityId capability, string? reason = null, string? adapter = null, string reasonCode = NativeErrorCodes.NotSupported) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.Unsupported,
        ReasonCode = reasonCode,
        Reason = reason ?? $"'{capability}' is not supported on this platform or host.",
        Adapter = adapter,
    };

    /// <summary>Creates an <see cref="CapabilityAvailability.Unknown"/> status.</summary>
    public static CapabilityStatus Unknown(CapabilityId capability, string? reason = null, string? adapter = null) => new()
    {
        Capability = capability,
        Availability = CapabilityAvailability.Unknown,
        Reason = reason,
        Adapter = adapter,
    };

    /// <summary>Builds a status from an error returned by an operation.</summary>
    public static CapabilityStatus FromError(NativeError error, string? adapter = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var availability = error.Status switch
        {
            NativeResultStatus.Unsupported => CapabilityAvailability.Unsupported,
            NativeResultStatus.PermissionDenied => CapabilityAvailability.PermissionDenied,
            NativeResultStatus.Unavailable => CapabilityAvailability.Unavailable,
            _ => CapabilityAvailability.Unknown,
        };

        return new CapabilityStatus
        {
            Capability = error.Capability ?? new CapabilityId("unknown"),
            Availability = availability,
            ReasonCode = error.Code,
            Reason = error.Message,
            Adapter = adapter,
        };
    }

    /// <summary>Returns a copy with additional features.</summary>
    public CapabilityStatus WithFeatures(params IEnumerable<string> features) => this with { Features = CreateFeatureSet(Features.Concat(features)) };

    private static HashSet<string> CreateFeatureSet(IEnumerable<string>? features) =>
        features is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(features, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() =>
        Reason is null ? $"{Capability}: {Availability}" : $"{Capability}: {Availability} ({Reason})";
}
