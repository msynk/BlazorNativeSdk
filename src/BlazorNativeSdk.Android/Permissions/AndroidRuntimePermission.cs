using Android.Content;
using Android.Content.PM;
using AndroidX.Core.Content;
using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions;
using MauiPermissionStatus = Microsoft.Maui.ApplicationModel.PermissionStatus;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// A Microsoft.Maui.Essentials permission for an arbitrary set of Android runtime permissions. Requests go through
/// Essentials, so results are delivered by <c>Platform.OnRequestPermissionsResult</c> (wired automatically by MAUI).
/// </summary>
/// <remarks>
/// Essentials' own <c>Permissions.Photos</c> declares no Android permissions and always reports granted, so photos (and
/// partial access on Android 14+) are modeled here.
/// </remarks>
internal sealed class AndroidRuntimePermission : MauiPermissions.BasePlatformPermission
{
    private readonly (string androidPermission, bool isRuntime)[] _required;
    private readonly string? _partialAccess;

    public AndroidRuntimePermission(IReadOnlyList<string> permissions, string? partialAccessPermission = null)
    {
        _required = [.. permissions.Select(p => (p, true))];
        _partialAccess = partialAccessPermission is not null && MauiPermissions.IsDeclaredInManifest(partialAccessPermission)
            ? partialAccessPermission
            : null;
    }

    /// <inheritdoc />
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions => _required;

    /// <summary>Checks the permissions; reports <see cref="MauiPermissionStatus.Limited"/> when only partial access is granted.</summary>
    public override async Task<MauiPermissionStatus> CheckStatusAsync()
    {
        var status = await base.CheckStatusAsync().ConfigureAwait(false);
        if (status != MauiPermissionStatus.Granted && _partialAccess is not null && IsGranted(_partialAccess))
        {
            return MauiPermissionStatus.Limited;
        }

        return status;
    }

    /// <summary>Requests the permissions (plus the partial-access permission) and returns the resulting status. Main thread only.</summary>
    public override async Task<MauiPermissionStatus> RequestAsync()
    {
        if (await CheckStatusAsync().ConfigureAwait(true) == MauiPermissionStatus.Granted)
        {
            return MauiPermissionStatus.Granted;
        }

        string[] toRequest = _partialAccess is null
            ? [.. _required.Select(r => r.androidPermission)]
            : [.. _required.Select(r => r.androidPermission), _partialAccess];

        await DoRequest(toRequest).ConfigureAwait(true);
        return await CheckStatusAsync().ConfigureAwait(true);
    }

    /// <summary>Returns <see langword="true"/> when Android suggests showing a rationale for any of the permissions.</summary>
    public bool ShouldShowRationale(Android.App.Activity activity) =>
        _required.Any(r => AndroidX.Core.App.ActivityCompat.ShouldShowRequestPermissionRationale(activity, r.androidPermission));

    /// <summary>The first required permission that is not declared in the manifest, or <see langword="null"/>.</summary>
    public string? FindUndeclared() => _required.Select(r => r.androidPermission).FirstOrDefault(p => !MauiPermissions.IsDeclaredInManifest(p));

    private static bool IsGranted(string permission) =>
        ContextCompat.CheckSelfPermission(AndroidContext.App, permission) == Permission.Granted;
}

/// <summary>
/// Remembers which Android permissions this app has requested, so a denied permission can be told apart from one that
/// was never requested (Android reports both as denied).
/// </summary>
internal static class PermissionRequestTracker
{
    private const string PreferencesName = "blazornativesdk.permissions";

    public static bool WasRequested(IEnumerable<string> androidPermissions)
    {
        // SharedPreferences instances are process-wide singletons; their peers are not disposed.
        var preferences = AndroidContext.App.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        return preferences is not null && androidPermissions.Any(p => preferences.GetBoolean(p, false));
    }

    public static void MarkRequested(IEnumerable<string> androidPermissions)
    {
        var preferences = AndroidContext.App.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        using var editor = preferences?.Edit();
        if (editor is null)
        {
            return;
        }

        foreach (var permission in androidPermissions)
        {
            editor.PutBoolean(permission, true);
        }

        editor.Apply();
    }
}
