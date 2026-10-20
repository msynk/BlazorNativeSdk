using System.Collections.Concurrent;
using System.Text;

namespace BlazorNativeSdk.Storage;

/// <summary>A non-durable <see cref="INativeStateStore"/> used in tests and as the fallback when no durable store is registered.</summary>
public sealed class InMemoryStateStore : INativeStateStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    /// <inheritdoc />
    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_values.TryRemove(key, out _));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetKeysAsync(string prefix, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
}

/// <summary>
/// A durable <see cref="INativeStateStore"/> that keeps one JSON file per key in a directory. Writes are atomic
/// (write to a temporary file, then rename) so a crash never leaves a half-written document.
/// </summary>
public sealed class FileStateStore : INativeStateStore
{
    private const string Extension = ".json";
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Creates a store rooted at <paramref name="directory"/>.</summary>
    public FileStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The storage directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = GetPath(key);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, value, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> GetKeysAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            var keys = new List<string>();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (TryDecode(name, out var key) && key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keys.Add(key);
                }
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetPath(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Path.Combine(Directory, Encode(key) + Extension);
    }

    // Keys are encoded with URL-safe Base64 so that any key maps to a valid, reversible, case-preserving file name.
    private static string Encode(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string name, out string key)
    {
        key = string.Empty;
        var base64 = name.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            key = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
