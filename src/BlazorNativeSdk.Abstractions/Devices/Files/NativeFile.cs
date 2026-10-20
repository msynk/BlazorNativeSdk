namespace BlazorNativeSdk.Devices;

/// <summary>
/// A file produced or consumed by a native capability (picked, captured, shared, ...). The content may live
/// on the local file system, in memory, or inside the browser (a JavaScript <c>Blob</c>), so always read it
/// through <see cref="OpenReadAsync"/> rather than assuming a path exists.
/// </summary>
/// <remarks>
/// Dispose files you no longer need: browser-backed files hold a reference to a JavaScript object and temporary
/// files created by capture operations are deleted on disposal when the adapter owns them.
/// </remarks>
public abstract class NativeFile : IAsyncDisposable
{
    /// <summary>The default maximum number of bytes <see cref="OpenReadAsync"/> allows (100 MB).</summary>
    public const long DefaultMaxReadSize = 100L * 1024 * 1024;

    /// <summary>Initializes a new file description.</summary>
    protected NativeFile(string name, string? contentType, long? size, DateTimeOffset? lastModified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? MimeTypes.FromFileName(name) : contentType;
        Size = size;
        LastModified = lastModified;
    }

    /// <summary>The file name including extension, without directory information.</summary>
    public string Name { get; }

    /// <summary>The MIME type when known.</summary>
    public string? ContentType { get; }

    /// <summary>The size in bytes when known.</summary>
    public long? Size { get; }

    /// <summary>The last modification time when known.</summary>
    public DateTimeOffset? LastModified { get; }

    /// <summary>
    /// The absolute path when the file exists on the local file system and the app may access it directly;
    /// <see langword="null"/> for browser-backed and in-memory files.
    /// </summary>
    public virtual string? FullPath => null;

    /// <summary>Opens a read-only stream over the content.</summary>
    /// <param name="maxAllowedSize">Maximum number of bytes that may be read; protects against unexpectedly large inputs.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="IOException">The file exceeds <paramref name="maxAllowedSize"/> or cannot be read.</exception>
    public abstract ValueTask<Stream> OpenReadAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default);

    /// <summary>Reads the whole content into memory.</summary>
    public virtual async ValueTask<byte[]> ReadAllBytesAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default)
    {
        var stream = await OpenReadAsync(maxAllowedSize, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(Size is > 0 and < int.MaxValue ? (int)Size.Value : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maxAllowedSize)
            {
                throw new IOException($"File '{Name}' exceeds the maximum allowed size of {maxAllowedSize} bytes.");
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Releases resources associated with the file.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates a file backed by a path on the local file system.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="contentType">The MIME type; inferred from the extension when omitted.</param>
    /// <param name="deleteOnDispose">Delete the file when the <see cref="NativeFile"/> is disposed (for temporary files).</param>
    public static NativeFile FromPath(string path, string? contentType = null, bool deleteOnDispose = false) =>
        new PhysicalNativeFile(path, contentType, deleteOnDispose);

    /// <summary>Creates a file backed by an in-memory buffer.</summary>
    public static NativeFile FromBytes(string name, ReadOnlyMemory<byte> content, string? contentType = null) =>
        new MemoryNativeFile(name, content, contentType);

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({ContentType ?? "unknown type"}, {(Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")} bytes)";
}

/// <summary>A <see cref="NativeFile"/> stored on the local file system.</summary>
public sealed class PhysicalNativeFile : NativeFile
{
    private readonly bool _deleteOnDispose;

    /// <summary>Creates a file wrapper for <paramref name="path"/>.</summary>
    public PhysicalNativeFile(string path, string? contentType = null, bool deleteOnDispose = false)
        : base(Path.GetFileName(path), contentType, TryGetLength(path), TryGetLastWrite(path))
    {
        FullPath = Path.GetFullPath(path);
        _deleteOnDispose = deleteOnDispose;
    }

    /// <inheritdoc />
    public override string FullPath { get; }

    /// <inheritdoc />
    public override ValueTask<Stream> OpenReadAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(FullPath);
        if (info.Exists && info.Length > maxAllowedSize)
        {
            throw new IOException($"File '{Name}' ({info.Length} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");
        }

        Stream stream = new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        if (_deleteOnDispose)
        {
            try
            {
                File.Delete(FullPath);
            }
            catch (IOException)
            {
                // Best effort: the file may still be open elsewhere.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return base.DisposeAsync();
    }

    private static long? TryGetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetLastWrite(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>A <see cref="NativeFile"/> stored in memory.</summary>
public sealed class MemoryNativeFile : NativeFile
{
    private readonly ReadOnlyMemory<byte> _content;

    /// <summary>Creates an in-memory file.</summary>
    public MemoryNativeFile(string name, ReadOnlyMemory<byte> content, string? contentType = null, DateTimeOffset? lastModified = null)
        : base(name, contentType, content.Length, lastModified)
    {
        _content = content;
    }

    /// <summary>The file content.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <inheritdoc />
    public override ValueTask<Stream> OpenReadAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_content.Length > maxAllowedSize)
        {
            throw new IOException($"File '{Name}' ({_content.Length} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");
        }

        Stream stream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(_content, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(_content.ToArray(), writable: false);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public override ValueTask<byte[]> ReadAllBytesAsync(long maxAllowedSize = DefaultMaxReadSize, CancellationToken cancellationToken = default)
    {
        if (_content.Length > maxAllowedSize)
        {
            throw new IOException($"File '{Name}' ({_content.Length} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");
        }

        return ValueTask.FromResult(_content.ToArray());
    }
}
