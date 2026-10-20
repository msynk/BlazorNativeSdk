using System.Diagnostics.CodeAnalysis;
using BlazorNativeSdk.Desktop.Internal;
using BlazorNativeSdk.Devices;
using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Desktop;

/// <summary>Turns desktop command-line arguments into <see cref="AppActivation"/>s.</summary>
/// <remarks>
/// <para>
/// A URL protocol launch passes exactly one argument, the URL. When the first argument is a URL whose scheme is in
/// <see cref="DeepLinkOptions.Schemes"/>, the remaining arguments are ignored (and a warning is logged): they can only come
/// from quotes injected into a crafted URL, for example <c>myapp://x" "C:\payload.txt</c>. The activation then carries the
/// URL alone in <see cref="AppActivation.Arguments"/>.
/// </para>
/// <para>Otherwise these rules apply to each argument:</para>
/// <list type="number">
/// <item>An absolute URI whose scheme is in <see cref="DeepLinkOptions.Schemes"/>, or an <c>https</c> URI whose host is in
/// <see cref="DeepLinkOptions.Hosts"/>, becomes one <see cref="ActivationKind.DeepLink"/> activation.</item>
/// <item>A path to an existing file (relative paths resolve against the launching process's working directory) is
/// collected into a single <see cref="ActivationKind.File"/> activation. On Windows, UNC and device paths
/// (<c>\\server\share\file</c>, <c>\\?\UNC\...</c>, <c>\\.\...</c>) are never probed, because that would send the user's
/// credentials to the server; they stay plain arguments.</item>
/// <item>When neither applies to any argument, one activation of the fallback kind
/// (<see cref="ActivationKind.SecondaryInstance"/> for forwarded launches, <see cref="ActivationKind.Launch"/> for the
/// process's own launch) carries the arguments.</item>
/// </list>
/// <para>
/// Every activation carries all <see cref="AppActivation.Arguments"/>. URLs with schemes or hosts that are not allow-listed
/// are never turned into deep links, so a crafted argument cannot drive navigation; handlers still see it in the arguments.
/// </para>
/// </remarks>
public static partial class ActivationArguments
{
    /// <summary>The <see cref="AppActivation.Source"/> of activations produced from command-line arguments.</summary>
    public const string Source = "desktop.command-line";

    /// <summary>Parses arguments into activations.</summary>
    /// <param name="arguments">The arguments, without the executable path (the <c>args</c> of <c>Main</c>).</param>
    /// <param name="deepLinks">The allow-listed deep-link schemes and hosts.</param>
    /// <param name="fallbackKind">The kind used when no argument is a deep link or a file.</param>
    /// <param name="isColdStart">Whether these arguments launched the current process.</param>
    /// <param name="workingDirectory">The directory relative paths resolve against. Defaults to the current directory.</param>
    /// <param name="logger">Logs ignored arguments of protocol launches.</param>
    public static IReadOnlyList<AppActivation> Parse(
        IReadOnlyList<string> arguments,
        DeepLinkOptions deepLinks,
        ActivationKind fallbackKind = ActivationKind.SecondaryInstance,
        bool isColdStart = false,
        string? workingDirectory = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(deepLinks);
        var args = arguments.Where(a => a is not null).ToArray();
        if (args.Length > 0 && TryGetDeepLink(args[0], deepLinks, out var protocolUri) && deepLinks.Schemes.Contains(protocolUri.Scheme))
        {
            // A protocol launch: the URL is the only legitimate argument.
            if (args.Length > 1 && logger is not null)
            {
                LogExtraProtocolArguments(logger, protocolUri.Scheme, args.Length - 1);
            }

            return
            [
                new AppActivation
                {
                    Kind = ActivationKind.DeepLink,
                    Uri = protocolUri,
                    Arguments = [args[0]],
                    IsColdStart = isColdStart,
                    Source = Source,
                },
            ];
        }

        var activations = new List<AppActivation>();
        var files = new List<NativeFile>();
        foreach (var argument in args)
        {
            if (TryGetDeepLink(argument, deepLinks, out var uri))
            {
                activations.Add(new AppActivation
                {
                    Kind = ActivationKind.DeepLink,
                    Uri = uri,
                    Arguments = args,
                    IsColdStart = isColdStart,
                    Source = Source,
                });
            }
            else if (TryGetExistingFile(argument, workingDirectory, out var path))
            {
                files.Add(NativeFile.FromPath(path));
            }
        }

        if (files.Count > 0)
        {
            activations.Add(new AppActivation
            {
                Kind = ActivationKind.File,
                Files = files,
                Arguments = args,
                IsColdStart = isColdStart,
                Source = Source,
            });
        }

        if (activations.Count == 0)
        {
            activations.Add(new AppActivation
            {
                Kind = fallbackKind,
                Arguments = args,
                IsColdStart = isColdStart,
                Source = Source,
            });
        }

        return activations;
    }

    /// <summary>Returns whether <paramref name="argument"/> is an allow-listed deep link.</summary>
    public static bool TryGetDeepLink(string? argument, DeepLinkOptions deepLinks, [NotNullWhen(true)] out Uri? uri)
    {
        ArgumentNullException.ThrowIfNull(deepLinks);
        uri = null;
        if (string.IsNullOrEmpty(argument) || argument.Length > 32 * 1024 || !Uri.TryCreate(argument, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        // Uri.TryCreate trims whitespace and accepts file paths (C:\..., /home/...) as file: URIs; require the argument to
        // literally start with the scheme so only real URLs qualify.
        if (!argument.StartsWith(candidate.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (deepLinks.Schemes.Contains(candidate.Scheme) ||
            (candidate.Scheme == Uri.UriSchemeHttps && candidate.Host.Length > 0 && deepLinks.Hosts.Contains(candidate.Host)))
        {
            uri = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetExistingFile(string argument, string? workingDirectory, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(argument) || argument.Length > 32 * 1024 || argument.Contains('\0', StringComparison.Ordinal) ||
            PathSafety.IsRemoteOrDevicePath(argument))
        {
            return false;
        }

        try
        {
            var candidate = Path.IsPathFullyQualified(argument)
                ? argument
                : Path.GetFullPath(argument, workingDirectory is not null && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : Environment.CurrentDirectory);

            // A forwarded working directory can be a UNC path; never probe the network with the user's credentials.
            if (PathSafety.IsRemoteOrDevicePath(candidate) || PathSafety.IsRemoteOrDevicePath(Path.GetFullPath(candidate)))
            {
                return false;
            }

            if (File.Exists(candidate))
            {
                path = Path.GetFullPath(candidate);
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    [LoggerMessage(EventId = 5402, Level = LogLevel.Warning, Message = "Ignored {Count} extra argument(s) after a '{Scheme}' URL: protocol launches pass only the URL, so they can only come from a crafted URL.")]
    private static partial void LogExtraProtocolArguments(ILogger logger, string scheme, int count);
}
