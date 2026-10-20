using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BlazorNativeSdk.Desktop.Interop;

namespace BlazorNativeSdk.Desktop.Ipc;

/// <summary>Derives OS endpoint names (pipe names, socket paths) from the app id, the channel and the user identity.</summary>
/// <remarks>
/// Names have the form <c>bns-{readable prefix}-{hash}</c>. The prefix is the sanitized, truncated <c>{appId}.{channel}</c>
/// and only aids diagnostics; the 64-bit hash covers the untruncated app id, channel and (for per-user channels) the user
/// identity (Windows SID / Unix effective uid), so truncation can never make two channels collide and two users never share
/// an endpoint name.
/// </remarks>
internal static class IpcEndpoints
{
    /// <summary>The maximum length of a logical channel name.</summary>
    public const int MaxChannelLength = 128;

    /// <summary><c>sockaddr_un.sun_path</c> is 104 bytes on macOS (108 on Linux) including the terminating NUL.</summary>
    internal const int MaxUnixSocketPathBytes = 103;

    private static readonly Lazy<string> s_userKey = new(ComputeUserKey);

    /// <summary>A stable identifier of the current user: the SID on Windows, the effective uid elsewhere.</summary>
    public static string UserKey => s_userKey.Value;

    /// <summary>Validates a logical channel name.</summary>
    public static bool IsValidChannel(string? channel) =>
        !string.IsNullOrWhiteSpace(channel) &&
        channel.Length <= MaxChannelLength &&
        !channel.Any(char.IsControl);

    /// <summary>Gets the Windows pipe name (without the <c>\\.\pipe\</c> prefix).</summary>
    public static string GetPipeName(string appId, string channel, bool currentUserOnly) =>
        BuildName(appId, channel, currentUserOnly ? UserKey : null, maxPrefixLength: 64);

    /// <summary>Gets the socket file name used on Unix (no directory).</summary>
    public static string GetSocketFileName(string appId, string channel, bool currentUserOnly) =>
        BuildName(appId, channel, currentUserOnly ? UserKey : null, maxPrefixLength: 20) + ".sock";

    /// <summary>
    /// Gets the candidate directories for per-user sockets, in order of preference: <c>$XDG_RUNTIME_DIR</c> on Linux
    /// (private, tmpfs, cleaned at logout), the per-user temporary directory (<c>$TMPDIR</c>, private on macOS) and
    /// <c>/tmp</c>. Each gets a private <c>bns-{user}</c> subdirectory. The first candidate whose full socket path fits
    /// in <c>sun_path</c> and whose directory is usable (<see cref="IsUsableSocketDirectory"/>) wins; the choice depends only
    /// on the file system, so clients and servers agree. A directory that another user created in a shared root (for
    /// example to squat <c>/tmp/bns-{user}</c>) is skipped, never reused.
    /// </summary>
    public static string? GetUnixSocketPath(string appId, string channel, bool currentUserOnly)
    {
        var fileName = GetSocketFileName(appId, channel, currentUserOnly);
        if (!currentUserOnly)
        {
            // Machine-wide channels live directly in the shared, sticky /tmp so every user computes the same path.
            var shared = Path.Combine("/tmp", fileName);
            return Encoding.UTF8.GetByteCount(shared) <= MaxUnixSocketPathBytes ? shared : null;
        }

        return SelectUnixSocketPath(GetUnixRoots(), "bns-" + Hash(UserKey, 4), fileName);
    }

    /// <summary>Picks the first root whose socket path fits in <c>sun_path</c> and whose per-user directory is usable.</summary>
    internal static string? SelectUnixSocketPath(IEnumerable<string> roots, string directoryName, string fileName)
    {
        foreach (var root in roots)
        {
            var directory = Path.Combine(root, directoryName);
            var candidate = Path.Combine(directory, fileName);
            if (Encoding.UTF8.GetByteCount(candidate) <= MaxUnixSocketPathBytes && IsUsableSocketDirectory(directory))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns whether a per-user socket directory may be used: nothing exists at the path yet (the server creates it), or it
    /// is a real directory (not a symbolic link) owned by the effective user with mode 0700. Directories that cannot be
    /// inspected are not usable.
    /// </summary>
    internal static bool IsUsableSocketDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        var errno = UnixInterop.LStat(directory, out var status);
        return errno == UnixInterop.ErrnoNoEntry || (errno == 0 && UnixInterop.IsPrivateDirectory(status));
    }

    internal static string BuildName(string appId, string channel, string? userKey, int maxPrefixLength)
    {
        var prefix = Sanitize(appId + "." + channel, maxPrefixLength);
        var hash = Hash(appId + "\n" + channel + "\n" + (userKey ?? "*machine*"), 8);
        return "bns-" + prefix + "-" + hash;
    }

    internal static string Sanitize(string value, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (builder.Length == maxLength)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        return builder.ToString();
    }

    private static string Hash(string value, int bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
        return Convert.ToHexStringLower(digest[..bytes]);
    }

    private static IEnumerable<string> GetUnixRoots()
    {
        if (OperatingSystem.IsLinux() &&
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime &&
            Path.IsPathFullyQualified(runtime) &&
            Directory.Exists(runtime))
        {
            yield return runtime;
        }

        var temp = Path.GetTempPath();
        if (Path.IsPathFullyQualified(temp) && Directory.Exists(temp))
        {
            yield return temp;
        }

        yield return "/tmp";
    }

    private static string ComputeUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? Environment.UserDomainName + "\\" + Environment.UserName;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return "uid:" + UnixInterop.geteuid().ToString(CultureInfo.InvariantCulture);
        }

        return "user:" + Environment.UserName;
    }
}
