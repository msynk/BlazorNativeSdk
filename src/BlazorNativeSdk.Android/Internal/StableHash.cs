namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// A deterministic 32-bit FNV-1a hash used to derive <c>PendingIntent</c> request codes from string ids. Unlike
/// <see cref="string.GetHashCode()"/> it is stable across processes, which matters because alarms and notifications
/// outlive the process that created them.
/// </summary>
internal static class StableHash
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    /// <summary>Hashes the parts (separated by a NUL character) to a non-negative integer.</summary>
    public static int Compute(params ReadOnlySpan<string?> parts)
    {
        var hash = OffsetBasis;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                hash = (hash ^ 0) * Prime;
            }

            foreach (var c in parts[i] ?? string.Empty)
            {
                hash = (hash ^ (byte)c) * Prime;
                hash = (hash ^ (byte)(c >> 8)) * Prime;
            }
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}
