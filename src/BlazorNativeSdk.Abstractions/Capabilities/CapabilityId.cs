namespace BlazorNativeSdk;

/// <summary>
/// Identifies a capability such as <c>camera</c> or <c>desktop.tray</c>. Identifiers are case-insensitive.
/// Well-known identifiers are exposed by <see cref="NativeCapabilities"/>; adapters and applications can define their own.
/// </summary>
public readonly struct CapabilityId : IEquatable<CapabilityId>
{
    private readonly string? _name;

    /// <summary>Creates a capability identifier.</summary>
    public CapabilityId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>The normalized (lowercase) capability name.</summary>
    public string Name => _name ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(CapabilityId other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CapabilityId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(CapabilityId left, CapabilityId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(CapabilityId left, CapabilityId right) => !left.Equals(right);

    /// <summary>Creates an identifier from a string.</summary>
    public static implicit operator CapabilityId(string name) => new(name);
}
