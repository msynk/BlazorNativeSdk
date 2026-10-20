namespace BlazorNativeSdk.Devices;

/// <summary>Optional features reported by <see cref="IClipboardService"/>.</summary>
public static class ClipboardFeatures
{
    /// <summary>Reading text.</summary>
    public const string ReadText = "read-text";

    /// <summary>Writing text.</summary>
    public const string WriteText = "write-text";

    /// <summary>The <see cref="IClipboardService.ContentChanged"/> event is raised.</summary>
    public const string ChangeNotification = "change-notification";
}

/// <summary>Reads and writes the system clipboard.</summary>
/// <remarks>
/// Browsers allow writing from a user gesture and may prompt (or refuse) when reading. Reading on iOS 16+ may show a
/// system paste-permission banner. Check <see cref="CapabilityStatus.Features"/> for <see cref="ClipboardFeatures"/>.
/// </remarks>
public interface IClipboardService : INativeCapability
{
    /// <summary>Reads text from the clipboard.</summary>
    /// <returns>The text, or a successful <see langword="null"/> when the clipboard holds no text.</returns>
    ValueTask<NativeResult<string?>> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes text to the clipboard; <see langword="null"/> clears it.</summary>
    ValueTask<NativeResult> SetTextAsync(string? text, CancellationToken cancellationToken = default);

    /// <summary>Raised when the clipboard content changes, when the <see cref="ClipboardFeatures.ChangeNotification"/> feature is supported.</summary>
    event EventHandler? ContentChanged;
}
