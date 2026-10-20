namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>Shared constants of the Windows adapter.</summary>
internal static class WindowsAdapter
{
    /// <summary>The adapter name reported in <see cref="CapabilityStatus.Adapter"/>.</summary>
    public const string Name = "windows";

    /// <summary>Error code: the operation is not possible from an elevated (administrator) process.</summary>
    public const string ElevatedProcessCode = "windows.elevated-process";

    /// <summary>Sanitizes an id for use as a folder name.</summary>
    public static string ToFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
            }
        });
    }
}
