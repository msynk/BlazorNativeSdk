using Foundation;
using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>
/// Entry point for the app delegate's background <c>URLSession</c> callback, used by the iOS transfer adapter
/// (<see cref="AppleBackgroundTransferService"/>).
/// </summary>
/// <remarks>
/// When transfers finish while the app is suspended or terminated, iOS launches it in the background and calls
/// <c>application:handleEventsForBackgroundURLSession:completionHandler:</c>. .NET MAUI does not expose that callback as a
/// lifecycle event, so export it from the app delegate (in a <c>UIApplicationDelegate</c> subclass, override
/// <c>HandleEventsForBackgroundUrl</c> instead):
/// <code>
/// [Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
/// public void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
/// {
///     if (!AppleBackgroundTransfers.HandleEventsForBackgroundUrl(sessionIdentifier, completionHandler))
///     {
///         completionHandler();
///     }
/// }
/// </code>
/// The SDK calls the completion handler once it has processed and persisted the session's events, which lets iOS suspend
/// the app again and update its snapshot. Without the override the events are still processed, but iOS cannot tell when.
/// </remarks>
public static class AppleBackgroundTransfers
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<string, SessionHolder> s_sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Action> s_completionHandlers = new(StringComparer.Ordinal);

    /// <summary>The default background session identifier: <c>{CFBundleIdentifier}.bns.transfers</c>.</summary>
    public static string DefaultSessionIdentifier => AppleInfoPlist.BundleIdentifier + ".bns.transfers";

    /// <summary>
    /// Takes the completion handler iOS passes to <c>application:handleEventsForBackgroundURLSession:completionHandler:</c>
    /// when <paramref name="sessionIdentifier"/> is the SDK's transfer session.
    /// </summary>
    /// <returns><see langword="true"/> when the session belongs to the SDK (it then calls <paramref name="completionHandler"/>);
    /// otherwise the caller handles the session itself.</returns>
    public static bool HandleEventsForBackgroundUrl(string sessionIdentifier, Action completionHandler)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionIdentifier);
        ArgumentNullException.ThrowIfNull(completionHandler);
        lock (s_gate)
        {
            if (!s_sessions.ContainsKey(sessionIdentifier) && !string.Equals(sessionIdentifier, DefaultSessionIdentifier, StringComparison.Ordinal))
            {
                return false;
            }

            s_completionHandlers[sessionIdentifier] = completionHandler;
        }

        return true;
    }

    /// <summary>
    /// Returns the process-wide session for <paramref name="identifier"/>, creating it on first use, and routes its delegate
    /// callbacks to <paramref name="owner"/>. iOS allows one session per identifier and process, so containers created later
    /// (tests, a restarted host) take over the same session.
    /// </summary>
    internal static NSUrlSession Attach(string identifier, AppleBackgroundTransferService owner)
    {
        lock (s_gate)
        {
            if (!s_sessions.TryGetValue(identifier, out var holder))
            {
                var sessionDelegate = new SessionDelegate();
                using var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration(identifier);
                configuration.SessionSendsLaunchEvents = true;
                configuration.Discretionary = false;
                var queue = new NSOperationQueue { MaxConcurrentOperationCount = 1, Name = "BlazorNativeSdk.Transfers" };
                var session = NSUrlSession.FromConfiguration(configuration, (INSUrlSessionDelegate)sessionDelegate, queue)
                    ?? throw new InvalidOperationException($"The background URL session '{identifier}' could not be created.");
                holder = new SessionHolder(session, sessionDelegate);
                s_sessions[identifier] = holder;
            }

            holder.Delegate.Owner = owner;
            return holder.Session;
        }
    }

    /// <summary>Stops routing callbacks to <paramref name="owner"/> (its container is being disposed).</summary>
    internal static void Detach(string identifier, AppleBackgroundTransferService owner)
    {
        lock (s_gate)
        {
            if (s_sessions.TryGetValue(identifier, out var holder) && ReferenceEquals(holder.Delegate.Owner, owner))
            {
                holder.Delegate.Owner = null;
            }
        }
    }

    /// <summary>Calls the completion handler iOS passed for <paramref name="identifier"/>, on the main thread.</summary>
    internal static void CompleteBackgroundEvents(string identifier)
    {
        Action? handler;
        lock (s_gate)
        {
            s_completionHandlers.Remove(identifier, out handler);
        }

        if (handler is not null)
        {
            MainThread.BeginInvokeOnMainThread(handler);
        }
    }

    private sealed record SessionHolder(NSUrlSession Session, SessionDelegate Delegate);

    // Callbacks arrive on the session's serial operation queue.
    private sealed class SessionDelegate : NSUrlSessionDownloadDelegate
    {
        private AppleBackgroundTransferService? _owner;

        public AppleBackgroundTransferService? Owner
        {
            get => Volatile.Read(ref _owner);
            set => Volatile.Write(ref _owner, value);
        }

        public override void DidWriteData(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, long bytesWritten, long totalBytesWritten, long totalBytesExpectedToWrite) =>
            Owner?.OnProgress(downloadTask, totalBytesWritten, totalBytesExpectedToWrite);

        public override void DidSendBodyData(NSUrlSession session, NSUrlSessionTask task, long bytesSent, long totalBytesSent, long totalBytesExpectedToSend) =>
            Owner?.OnProgress(task, totalBytesSent, totalBytesExpectedToSend);

        public override void DidFinishDownloading(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, NSUrl location) =>
            Owner?.OnDownloadFinished(downloadTask, location);

        public override void DidCompleteWithError(NSUrlSession session, NSUrlSessionTask task, NSError? error) =>
            Owner?.OnCompleted(task, error);

        public override void DidFinishEventsForBackgroundSession(NSUrlSession session)
        {
            if (Owner is { } owner)
            {
                owner.OnBackgroundEventsFinished();
            }
            else if (session.Configuration.Identifier is { } identifier)
            {
                CompleteBackgroundEvents(identifier);
            }
        }
    }
}
