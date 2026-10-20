namespace BlazorNativeSdk.Lifecycle;

/// <summary>Transfer direction.</summary>
public enum TransferDirection
{
    /// <summary>Download to a local file.</summary>
    Download,

    /// <summary>Upload a local file.</summary>
    Upload,
}

/// <summary>Transfer state.</summary>
public enum TransferStatus
{
    /// <summary>Waiting to start.</summary>
    Queued,

    /// <summary>Transferring.</summary>
    Running,

    /// <summary>Waiting for connectivity (or an unmetered network) before continuing.</summary>
    WaitingForNetwork,

    /// <summary>Interrupted; will retry after a backoff delay.</summary>
    Retrying,

    /// <summary>Completed successfully.</summary>
    Completed,

    /// <summary>Failed permanently.</summary>
    Failed,

    /// <summary>Cancelled.</summary>
    Cancelled,
}

/// <summary>A download request.</summary>
public sealed record DownloadRequest
{
    /// <summary>The source URL.</summary>
    public required Uri Uri { get; init; }

    /// <summary>The destination file path. Relative paths are resolved against the app's data directory.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>An id; generated when omitted. Enqueuing an id that is already active returns the existing transfer.</summary>
    public string? Id { get; init; }

    /// <summary>Request headers. Do not put long-lived secrets here: transfer state is persisted unencrypted.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Only transfer on unmetered networks.</summary>
    public bool RequireUnmeteredNetwork { get; init; }

    /// <summary>The maximum number of retries for transient failures.</summary>
    public int MaxRetries { get; init; } = 8;
}

/// <summary>An upload request.</summary>
public sealed record UploadRequest
{
    /// <summary>The target URL.</summary>
    public required Uri Uri { get; init; }

    /// <summary>The local file to upload.</summary>
    public required string SourcePath { get; init; }

    /// <summary>An id; generated when omitted.</summary>
    public string? Id { get; init; }

    /// <summary>The HTTP method, <c>POST</c> by default.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>The content type; inferred from the file name when omitted.</summary>
    public string? ContentType { get; init; }

    /// <summary>When set, the file is sent as <c>multipart/form-data</c> under this form field name; otherwise the body is the raw file.</summary>
    public string? MultipartFieldName { get; init; }

    /// <summary>Request headers. Do not put long-lived secrets here: transfer state is persisted unencrypted.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Only transfer on unmetered networks.</summary>
    public bool RequireUnmeteredNetwork { get; init; }

    /// <summary>The maximum number of retries for transient failures.</summary>
    public int MaxRetries { get; init; } = 8;
}

/// <summary>A snapshot of a transfer.</summary>
public sealed record TransferInfo
{
    /// <summary>The id.</summary>
    public required string Id { get; init; }

    /// <summary>The direction.</summary>
    public required TransferDirection Direction { get; init; }

    /// <summary>The remote URL.</summary>
    public required Uri Uri { get; init; }

    /// <summary>The local file path.</summary>
    public required string LocalPath { get; init; }

    /// <summary>The status.</summary>
    public required TransferStatus Status { get; init; }

    /// <summary>Bytes transferred so far.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>The total size when known.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The number of retries performed.</summary>
    public int Retries { get; init; }

    /// <summary>The last HTTP status code.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>The last error message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the transfer was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the transfer finished.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Progress between 0 and 1 when the total size is known.</summary>
    public double? Progress => TotalBytes is > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes.Value, 0, 1) : null;

    /// <summary><see langword="true"/> when the transfer reached a terminal state.</summary>
    public bool IsFinished => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled;
}

/// <summary>The guarantees a transfer adapter provides.</summary>
public sealed record TransferPolicy
{
    /// <summary>The adapter name.</summary>
    public required string Adapter { get; init; }

    /// <summary>Transfers continue while the app is in the background.</summary>
    public required bool ContinuesInBackground { get; init; }

    /// <summary>Transfers continue (or are completed by the OS) after the app process is terminated.</summary>
    public required bool ContinuesAfterTermination { get; init; }

    /// <summary>Interrupted downloads resume from the last byte when the server supports range requests.</summary>
    public required bool ResumesDownloads { get; init; }

    /// <summary>A short human-readable summary.</summary>
    public required string Summary { get; init; }
}

/// <summary>Durable, resumable file transfers that survive interruptions such as connectivity loss or app restarts.</summary>
public interface ITransferService : INativeCapability
{
    /// <summary>The guarantees provided by the current adapter.</summary>
    TransferPolicy Policy { get; }

    /// <summary>Queues a download.</summary>
    ValueTask<NativeResult<TransferInfo>> EnqueueDownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queues an upload.</summary>
    ValueTask<NativeResult<TransferInfo>> EnqueueUploadAsync(UploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a transfer and deletes partial downloads.</summary>
    ValueTask<NativeResult<bool>> CancelAsync(string transferId, CancellationToken cancellationToken = default);

    /// <summary>Restarts a failed transfer.</summary>
    ValueTask<NativeResult<TransferInfo>> RetryAsync(string transferId, CancellationToken cancellationToken = default);

    /// <summary>Lists known transfers.</summary>
    ValueTask<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits until a transfer reaches a terminal state.</summary>
    ValueTask<NativeResult<TransferInfo>> WaitForCompletionAsync(string transferId, CancellationToken cancellationToken = default);

    /// <summary>Raised on progress and status changes. May be raised on a background thread.</summary>
    event EventHandler<TransferInfo>? TransferChanged;
}
