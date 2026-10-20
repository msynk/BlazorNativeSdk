namespace BlazorNativeSdk.Devices;

/// <summary>Content to hand to the native share sheet.</summary>
public sealed record ShareRequest
{
    /// <summary>A title for the share sheet (Android chooser title, email subject, ...).</summary>
    public string? Title { get; init; }

    /// <summary>Text to share.</summary>
    public string? Text { get; init; }

    /// <summary>A link to share.</summary>
    public Uri? Uri { get; init; }

    /// <summary>Files to share. Check the <see cref="ShareFeatures.Files"/> feature first.</summary>
    public IReadOnlyList<NativeFile> Files { get; init; } = [];

    /// <summary>Creates a request that shares a link.</summary>
    public static ShareRequest ForUri(Uri uri, string? title = null) => new() { Uri = uri, Title = title };

    /// <summary>Creates a request that shares text.</summary>
    public static ShareRequest ForText(string text, string? title = null) => new() { Text = text, Title = title };

    /// <summary>Creates a request that shares files.</summary>
    public static ShareRequest ForFiles(string? title, params NativeFile[] files) => new() { Title = title, Files = files };
}

/// <summary>Optional features reported by <see cref="IShareService"/>.</summary>
public static class ShareFeatures
{
    /// <summary>Sharing text.</summary>
    public const string Text = "text";

    /// <summary>Sharing links.</summary>
    public const string Uri = "uri";

    /// <summary>Sharing files.</summary>
    public const string Files = "files";

    /// <summary>The platform reports whether the user completed or cancelled the share.</summary>
    public const string CompletionReporting = "completion-reporting";
}

/// <summary>Shows the native share sheet.</summary>
/// <remarks>
/// Not every platform reports what the user did in the share sheet. When the
/// <see cref="ShareFeatures.CompletionReporting"/> feature is absent, success means "the sheet was presented",
/// and dismissal cannot be distinguished from completion.
/// </remarks>
public interface IShareService : INativeCapability
{
    /// <summary>Shows the share sheet.</summary>
    /// <remarks>Browsers require this to be called from a user gesture.</remarks>
    ValueTask<NativeResult> ShareAsync(ShareRequest request, CancellationToken cancellationToken = default);
}
