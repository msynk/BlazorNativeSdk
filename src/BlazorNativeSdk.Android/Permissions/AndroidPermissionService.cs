using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IPermissionService"/> for Android, built on Microsoft.Maui.Essentials permissions.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="NativePermissions.Photos"/> maps to <c>READ_MEDIA_IMAGES</c> on Android 13+ (and reports
/// <see cref="PermissionStatus.Limited"/> when the user selected only some photos on Android 14+, if
/// <c>READ_MEDIA_VISUAL_USER_SELECTED</c> is declared) and to <c>READ_EXTERNAL_STORAGE</c> below.</item>
/// <item><see cref="NativePermissions.Notifications"/> maps to <c>POST_NOTIFICATIONS</c> on Android 13+. Below, no
/// permission exists and the status is <see cref="PermissionStatus.NotRequired"/> unless the user disabled the app's
/// notifications in settings (<see cref="PermissionStatus.Denied"/>).</item>
/// <item><see cref="NativePermissions.ExactAlarms"/> reflects <c>AlarmManager.canScheduleExactAlarms()</c> on Android 12+;
/// requesting it opens the system "Alarms &amp; reminders" page.</item>
/// <item>Any <c>android.permission.*</c> name is accepted as a raw runtime permission.</item>
/// </list>
/// Android reports "denied" before the first request too; the adapter remembers which permissions it requested and
/// reports <see cref="PermissionStatus.NotDetermined"/> until then. Permissions that are not declared in
/// AndroidManifest.xml produce <see cref="NativeResultStatus.Unavailable"/> with <see cref="NativeErrorCodes.MissingDeclaration"/>.
/// Non-MAUI apps must forward <c>OnRequestPermissionsResult</c> to <c>Platform.OnRequestPermissionsResult</c>.
/// </remarks>
public sealed partial class AndroidPermissionService : IPermissionService
{
    private const string AdapterName = "android";
    private const string ExactAlarmRequestKey = "blazornativesdk.exact-alarms";
    private static readonly string[] s_exactAlarmPermissions = [AndroidPermissionCatalog.ScheduleExactAlarm];
    private static readonly string[] s_exactAlarmRequestKeys = [ExactAlarmRequestKey];
    private readonly ILogger<AndroidPermissionService> _logger;

    /// <summary>Creates the service.</summary>
    public AndroidPermissionService(ILogger<AndroidPermissionService> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Permissions;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, "rationale", "app-settings"));

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct => CheckForAsync(Capability, permission, ct), cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct => RequestForAsync(Capability, permission, ct), cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync(Capability, ct =>
        {
            var plan = AndroidPermissionCatalog.Resolve(permission, AndroidContext.ApiLevel);
            if (plan.Kind == AndroidPermissionKind.Unsupported)
            {
                return ValueTask.FromResult<NativeResult<bool>>(UnsupportedPermission(Capability, permission));
            }

            if (plan.Kind != AndroidPermissionKind.Runtime || AndroidContext.CurrentActivity is not { } activity)
            {
                return ValueTask.FromResult<NativeResult<bool>>(false);
            }

            var runtime = new AndroidRuntimePermission(plan.AndroidPermissions, plan.PartialAccessPermission);
            return ValueTask.FromResult<NativeResult<bool>>(runtime.FindUndeclared() is null && runtime.ShouldShowRationale(activity));
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Opens the app's "App info" page in system settings.</remarks>
    public ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.TryAsync(Capability, _ => MainThread.InvokeOnMainThreadAsync(AppInfo.ShowSettingsUI), cancellationToken);

    /// <summary>Checks a permission and reports errors for <paramref name="capability"/>.</summary>
    internal static async ValueTask<NativeResult<PermissionStatus>> CheckForAsync(CapabilityId capability, NativePermission permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = AndroidPermissionCatalog.Resolve(permission, AndroidContext.ApiLevel);
        switch (plan.Kind)
        {
            case AndroidPermissionKind.Unsupported:
                return UnsupportedPermission(capability, permission);

            case AndroidPermissionKind.NotRequired:
                return PermissionStatus.NotRequired;

            case AndroidPermissionKind.LegacyNotifications:
                return AndroidPermissionMapper.MapLegacyNotifications(AreNotificationsEnabled());

            case AndroidPermissionKind.ExactAlarms:
                return CheckExactAlarms(capability);

            default:
                var runtime = new AndroidRuntimePermission(plan.AndroidPermissions, plan.PartialAccessPermission);
                if (runtime.FindUndeclared() is { } undeclared)
                {
                    return AndroidPermissionErrors.MissingDeclaration(capability, undeclared);
                }

                var status = AndroidPermissionMapper.Map(await runtime.CheckStatusAsync().ConfigureAwait(false));
                return Refine(status, plan, runtime, requestedNow: false);
        }
    }

    /// <summary>Requests a permission and reports errors for <paramref name="capability"/>.</summary>
    internal async ValueTask<NativeResult<PermissionStatus>> RequestForAsync(CapabilityId capability, NativePermission permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = AndroidPermissionCatalog.Resolve(permission, AndroidContext.ApiLevel);
        switch (plan.Kind)
        {
            case AndroidPermissionKind.Unsupported:
                return UnsupportedPermission(capability, permission);

            case AndroidPermissionKind.NotRequired:
                return PermissionStatus.NotRequired;

            case AndroidPermissionKind.LegacyNotifications:
                // No runtime prompt exists below Android 13; the user must re-enable notifications in settings.
                return AndroidPermissionMapper.MapLegacyNotifications(AreNotificationsEnabled());

            case AndroidPermissionKind.ExactAlarms:
                return await RequestExactAlarmsAsync(capability, cancellationToken).ConfigureAwait(false);

            default:
                var runtime = new AndroidRuntimePermission(plan.AndroidPermissions, plan.PartialAccessPermission);
                if (runtime.FindUndeclared() is { } undeclared)
                {
                    return AndroidPermissionErrors.MissingDeclaration(capability, undeclared);
                }

                if (AndroidContext.CurrentActivity is null)
                {
                    return AndroidContext.NoActivity(capability);
                }

                PermissionRequestTracker.MarkRequested(plan.AndroidPermissions);
                LogRequesting(_logger, plan.AndroidPermissions);
                try
                {
                    var result = await MainThread.InvokeOnMainThreadAsync(runtime.RequestAsync).WaitAsync(cancellationToken).ConfigureAwait(false);
                    return Refine(AndroidPermissionMapper.Map(result), plan, runtime, requestedNow: true);
                }
                catch (PermissionException ex)
                {
                    return EssentialsErrors.Map(capability, ex, permission);
                }
        }
    }

    /// <summary>Checks a permission and requests it when undecided; fails with <see cref="NativeResultStatus.PermissionDenied"/> otherwise.</summary>
    internal async ValueTask<NativeResult<PermissionStatus>> EnsureForAsync(CapabilityId capability, NativePermission permission, CancellationToken cancellationToken)
    {
        var check = await CheckForAsync(capability, permission, cancellationToken).ConfigureAwait(false);
        if (!check.IsSuccess || check.Value.IsGranted())
        {
            return check;
        }

        if (check.Value is PermissionStatus.Restricted)
        {
            return NativeError.PermissionDenied(capability, permission, check.Value);
        }

        var request = await RequestForAsync(capability, permission, cancellationToken).ConfigureAwait(false);
        if (!request.IsSuccess)
        {
            return request;
        }

        return request.Value.IsGranted() ? request : NativeError.PermissionDenied(capability, permission, request.Value);
    }

    internal static bool AreNotificationsEnabled()
    {
        using var manager = NotificationManagerCompat.From(AndroidContext.App);
        return manager?.AreNotificationsEnabled() ?? false;
    }

    internal static bool CanScheduleExactAlarms()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return true;
        }

        return AndroidContext.App.GetSystemService(Context.AlarmService) is AlarmManager alarms && alarms.CanScheduleExactAlarms();
    }

    private static PermissionStatus Refine(PermissionStatus status, AndroidPermissionPlan plan, AndroidRuntimePermission runtime, bool requestedNow)
    {
        // POST_NOTIFICATIONS can be granted while the user blocked all notifications for the app.
        if (status == PermissionStatus.Granted &&
            plan.AndroidPermissions.Contains(AndroidPermissionCatalog.PostNotifications, StringComparer.Ordinal) &&
            !AreNotificationsEnabled())
        {
            return PermissionStatus.Denied;
        }

        if (status != PermissionStatus.Denied)
        {
            return status;
        }

        var requested = requestedNow || PermissionRequestTracker.WasRequested(plan.AndroidPermissions);
        var rationale = AndroidContext.CurrentActivity is { } activity && runtime.ShouldShowRationale(activity);
        return AndroidPermissionMapper.Refine(status, requested, rationale);
    }

    private static NativeResult<PermissionStatus> CheckExactAlarms(CapabilityId capability)
    {
        if (!MauiPermissions.IsDeclaredInManifest(AndroidPermissionCatalog.ScheduleExactAlarm) &&
            !MauiPermissions.IsDeclaredInManifest(AndroidPermissionCatalog.UseExactAlarm))
        {
            return AndroidPermissionErrors.MissingDeclaration(capability, AndroidPermissionCatalog.ScheduleExactAlarm);
        }

        var canSchedule = CanScheduleExactAlarms();
        return AndroidPermissionMapper.MapExactAlarms(canSchedule, !canSchedule && PermissionRequestTracker.WasRequested(s_exactAlarmRequestKeys));
    }

    private async ValueTask<NativeResult<PermissionStatus>> RequestExactAlarmsAsync(CapabilityId capability, CancellationToken cancellationToken)
    {
        var check = CheckExactAlarms(capability);
        if (!check.IsSuccess || check.Value == PermissionStatus.Granted || !OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return check;
        }

        PermissionRequestTracker.MarkRequested(s_exactAlarmRequestKeys);
        LogRequesting(_logger, s_exactAlarmPermissions);
        using var intent = new Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm,
            Android.Net.Uri.Parse("package:" + AndroidContext.PackageName));
        try
        {
            await ActivityResultBridge.StartActivityForResultAsync(capability, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (ActivityNotFoundException ex)
        {
            return EssentialsErrors.Map(capability, ex);
        }

        return AndroidPermissionMapper.MapExactAlarms(CanScheduleExactAlarms(), requestedBefore: true);
    }

    private static NativeError UnsupportedPermission(CapabilityId capability, NativePermission permission) =>
        NativeError.Unsupported(capability,
            $"Android has no permission named '{permission}'. Use a NativePermissions value or a full 'android.permission.*' name.");

    [LoggerMessage(EventId = 3101, Level = LogLevel.Debug, Message = "Requesting Android permission(s) {Permissions}.")]
    private static partial void LogRequesting(ILogger logger, IReadOnlyList<string> permissions);
}
