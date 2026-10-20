using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed class SecureStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bns-windows-tests", Guid.NewGuid().ToString("N"));

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Set_get_remove_and_clear_round_trip()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.tests");
        Assert.Equal(SecureStorageProtection.OperatingSystem, storage.Protection);

        Assert.True((await storage.SetAsync("token", "s3cr3t-✓", Token)).IsSuccess);
        var read = await storage.GetAsync("token", Token);
        Assert.True(read.IsSuccess);
        Assert.Equal("s3cr3t-✓", read.Value);

        Assert.True((await storage.SetAsync("token", "replaced", Token)).IsSuccess);
        Assert.Equal("replaced", (await storage.GetAsync("token", Token)).Value);

        var removed = await storage.RemoveAsync("token", Token);
        Assert.True(removed.IsSuccess && removed.Value);
        Assert.False((await storage.RemoveAsync("token", Token)).Value);
        Assert.Null((await storage.GetAsync("token", Token)).Value);

        await storage.SetAsync("a", "1", Token);
        await storage.SetAsync("b", "2", Token);
        Assert.True((await storage.ClearAsync(Token)).IsSuccess);
        Assert.Null((await storage.GetAsync("a", Token)).Value);
        Assert.Null((await storage.GetAsync("b", Token)).Value);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Missing_key_is_a_successful_null()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.tests");
        var result = await storage.GetAsync("missing", Token);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Keys_are_isolated_and_values_are_encrypted_at_rest()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.tests");
        await storage.SetAsync("first", "value-one", Token);
        await storage.SetAsync("FIRST", "value-two", Token);

        Assert.Equal("value-one", (await storage.GetAsync("first", Token)).Value);
        Assert.Equal("value-two", (await storage.GetAsync("FIRST", Token)).Value);

        var files = Directory.GetFiles(_directory);
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, Token);
            Assert.DoesNotContain("value-", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("first", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
        }

        await storage.RemoveAsync("first", Token);
        Assert.Equal("value-two", (await storage.GetAsync("FIRST", Token)).Value);
    }

    [Fact]
    public async Task A_blob_moved_to_another_key_does_not_decrypt()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.tests");
        await storage.SetAsync("alpha", "alpha-secret", Token);
        await storage.SetAsync("beta", "beta-secret", Token);

        File.Copy(FileOf(storage, "alpha"), FileOf(storage, "beta"), overwrite: true);

        var result = await storage.GetAsync("beta", Token);
        Assert.False(result.IsSuccess);
        Assert.Equal(NativeResultStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Another_app_id_cannot_read_the_values()
    {
        var mine = new DpapiSecureStorage(_directory, "com.example.mine");
        await mine.SetAsync("token", "mine", Token);

        var theirs = new DpapiSecureStorage(_directory, "com.example.theirs");
        var result = await theirs.GetAsync("token", Token);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Concurrent_writers_never_corrupt_values()
    {
        var storage = new DpapiSecureStorage(_directory, "com.example.tests");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => storage.SetAsync("shared", "value-" + i, Token).AsTask()));
        var result = await storage.GetAsync("shared", Token);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("value-", result.Value, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string FileOf(DpapiSecureStorage storage, string key)
    {
        var path = Path.Combine(storage.Directory, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".dat");
        Assert.True(File.Exists(path), $"Expected the value of '{key}' in {path}.");
        return path;
    }
}
