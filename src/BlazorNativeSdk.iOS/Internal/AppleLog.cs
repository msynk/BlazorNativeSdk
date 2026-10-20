using BlazorNativeSdk.Lifecycle;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Source-generated log messages used by static entry points (event ids 3000-3099).</summary>
internal static partial class AppleLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug, Message = "Published {Count} activation(s) buffered before the service provider was attached.")]
    public static partial void FlushedActivations(ILogger logger, int count);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "No IActivationPublisher is registered; {Kind} activation dropped.")]
    public static partial void NoActivationPublisher(ILogger logger, ActivationKind kind);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Publishing a {Kind} activation failed.")]
    public static partial void ActivationPublishFailed(ILogger logger, ActivationKind kind, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Another UNUserNotificationCenter delegate ({DelegateType}) is installed; notification taps are not routed unless it calls NativeSdkNotificationCenterDelegate.TryPublishActivation.")]
    public static partial void ForeignNotificationDelegate(ILogger logger, string delegateType);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Background task identifier '{Identifier}' is not listed in BGTaskSchedulerPermittedIdentifiers; its launch handler was not registered.")]
    public static partial void BackgroundTaskNotPermitted(ILogger logger, string identifier);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "Registering the launch handler for background task '{Identifier}' failed.")]
    public static partial void BackgroundTaskRegistrationFailed(ILogger logger, string identifier, Exception? exception);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "The system launched background task '{Identifier}'.")]
    public static partial void BackgroundTaskStarted(ILogger logger, string identifier);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Information, Message = "Background task '{Identifier}' finished: {Executed} work item(s) ran, success = {Success}.")]
    public static partial void BackgroundTaskCompleted(ILogger logger, string identifier, int executed, bool success);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Warning, Message = "The system expired background task '{Identifier}' before it completed.")]
    public static partial void BackgroundTaskExpired(ILogger logger, string identifier);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Error, Message = "Background task '{Identifier}' failed.")]
    public static partial void BackgroundTaskFailed(ILogger logger, string identifier, Exception exception);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Debug, Message = "Ignored an activation that could not be converted: {Reason}.")]
    public static partial void ActivationIgnored(ILogger logger, string reason);
}
