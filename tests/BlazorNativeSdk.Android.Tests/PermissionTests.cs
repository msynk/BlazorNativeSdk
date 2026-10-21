using Microsoft.Maui.ApplicationModel;
using C = BlazorNativeSdk.AndroidPlatform.AndroidPermissionCatalog;
using EssentialsStatus = Microsoft.Maui.ApplicationModel.PermissionStatus;
using PermissionStatus = BlazorNativeSdk.PermissionStatus;

namespace BlazorNativeSdk.AndroidPlatform.Tests;

public sealed class PermissionCatalogTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(34)]
    public void Camera_and_microphone_are_runtime_permissions(int apiLevel)
    {
        AssertRuntime(C.Resolve(NativePermissions.Camera, apiLevel), C.Camera);
        AssertRuntime(C.Resolve(NativePermissions.Microphone, apiLevel), C.RecordAudio);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void Photos_use_external_storage_before_Android_13(int apiLevel) =>
        AssertRuntime(C.Resolve(NativePermissions.Photos, apiLevel), C.ReadExternalStorage);

    [Fact]
    public void Photos_use_media_images_on_Android_13() =>
        AssertRuntime(C.Resolve(NativePermissions.Photos, 33), C.ReadMediaImages);

    [Theory]
    [InlineData(34)]
    [InlineData(35)]
    public void Photos_report_partial_access_from_Android_14(int apiLevel)
    {
        var plan = C.Resolve(NativePermissions.Photos, apiLevel);

        Assert.Equal([C.ReadMediaImages], plan.AndroidPermissions);
        Assert.Equal(C.ReadMediaVisualUserSelected, plan.PartialAccessPermission);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(28, false)]
    [InlineData(29, true)]
    [InlineData(34, true)]
    public void Adding_photos_needs_no_permission_from_Android_10(int apiLevel, bool notRequired) =>
        AssertNotRequiredOr(C.Resolve(NativePermissions.PhotosAddOnly, apiLevel), notRequired, C.WriteExternalStorage);

    [Theory]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void Storage_read_is_legacy_up_to_Android_12(int apiLevel, bool notRequired) =>
        AssertNotRequiredOr(C.Resolve(NativePermissions.StorageRead, apiLevel), notRequired, C.ReadExternalStorage);

    // Scoped storage (Android 10, API 29) ends the need for WRITE_EXTERNAL_STORAGE, which the manifest declares with maxSdkVersion="28".
    [Theory]
    [InlineData(24, false)]
    [InlineData(28, false)]
    [InlineData(29, true)]
    [InlineData(30, true)]
    [InlineData(34, true)]
    public void Storage_write_is_legacy(int apiLevel, bool notRequired) =>
        AssertNotRequiredOr(C.Resolve(NativePermissions.StorageWrite, apiLevel), notRequired, C.WriteExternalStorage);

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void Notifications_before_Android_13_have_no_runtime_permission(int apiLevel)
    {
        var plan = C.Resolve(NativePermissions.Notifications, apiLevel);

        Assert.Equal(AndroidPermissionKind.LegacyNotifications, plan.Kind);
        Assert.Empty(plan.AndroidPermissions);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(35)]
    public void Notifications_need_POST_NOTIFICATIONS_from_Android_13(int apiLevel) =>
        AssertRuntime(C.Resolve(NativePermissions.Notifications, apiLevel), C.PostNotifications);

    [Fact]
    public void Exact_alarms_are_free_before_Android_12() =>
        Assert.Equal(AndroidPermissionKind.NotRequired, C.Resolve(NativePermissions.ExactAlarms, 30).Kind);

    [Theory]
    [InlineData(31)]
    [InlineData(34)]
    public void Exact_alarms_are_a_special_app_op_from_Android_12(int apiLevel)
    {
        var plan = C.Resolve(NativePermissions.ExactAlarms, apiLevel);

        Assert.Equal(AndroidPermissionKind.ExactAlarms, plan.Kind);
        Assert.Equal([C.ScheduleExactAlarm, C.UseExactAlarm], plan.AndroidPermissions);
    }

    [Theory]
    [InlineData("biometrics")]
    [InlineData("clipboard-read")]
    public void Some_operations_are_never_gated(string name) =>
        Assert.Equal(AndroidPermissionKind.NotRequired, C.Resolve(new NativePermission(name), 34).Kind);

    [Fact]
    public void Well_known_names_are_case_insensitive() =>
        AssertRuntime(C.Resolve(new NativePermission("CAMERA"), 34), C.Camera);

    [Fact]
    public void Any_android_permission_name_is_a_raw_runtime_permission() =>
        AssertRuntime(C.Resolve(new NativePermission("android.permission.ACCESS_FINE_LOCATION"), 34), "android.permission.ACCESS_FINE_LOCATION");

    [Theory]
    [InlineData("location")]
    [InlineData("android.permission.")]
    [InlineData("ANDROID.PERMISSION.CAMERA")]
    [InlineData("")]
    public void Unknown_names_are_unsupported(string name) =>
        Assert.Equal(AndroidPermissionKind.Unsupported, C.Resolve(new NativePermission(name), 34).Kind);

    [Fact]
    public void A_default_permission_is_unsupported() =>
        Assert.Equal(AndroidPermissionKind.Unsupported, C.Resolve(default, 34).Kind);

    private static void AssertRuntime(AndroidPermissionPlan plan, string androidPermission)
    {
        Assert.Equal(AndroidPermissionKind.Runtime, plan.Kind);
        Assert.Equal([androidPermission], plan.AndroidPermissions);
        Assert.Null(plan.PartialAccessPermission);
    }

    private static void AssertNotRequiredOr(AndroidPermissionPlan plan, bool notRequired, string androidPermission)
    {
        if (notRequired)
        {
            Assert.Equal(AndroidPermissionKind.NotRequired, plan.Kind);
            Assert.Empty(plan.AndroidPermissions);
        }
        else
        {
            AssertRuntime(plan, androidPermission);
        }
    }
}

public sealed class PermissionStatusTests
{
    [Theory]
    [InlineData(EssentialsStatus.Granted, PermissionStatus.Granted)]
    [InlineData(EssentialsStatus.Denied, PermissionStatus.Denied)]
    [InlineData(EssentialsStatus.Disabled, PermissionStatus.Restricted)]
    [InlineData(EssentialsStatus.Restricted, PermissionStatus.Restricted)]
    [InlineData(EssentialsStatus.Limited, PermissionStatus.Limited)]
    [InlineData(EssentialsStatus.Unknown, PermissionStatus.Unknown)]
    public void Maps_Essentials_states(EssentialsStatus status, PermissionStatus expected) =>
        Assert.Equal(expected, AndroidPermissionMapper.Map(status));

    [Theory]
    [InlineData(PermissionStatus.Denied, false, false, PermissionStatus.NotDetermined)]   // never asked: NotDetermined until first requested
    [InlineData(PermissionStatus.Denied, true, false, PermissionStatus.Denied)]           // asked and denied ("don't ask again")
    [InlineData(PermissionStatus.Denied, false, true, PermissionStatus.Denied)]           // Android suggests a rationale: denied once
    [InlineData(PermissionStatus.Denied, true, true, PermissionStatus.Denied)]
    [InlineData(PermissionStatus.Granted, false, false, PermissionStatus.Granted)]
    [InlineData(PermissionStatus.Limited, false, false, PermissionStatus.Limited)]
    [InlineData(PermissionStatus.Restricted, false, false, PermissionStatus.Restricted)]
    public void Denied_before_the_first_request_is_not_determined(PermissionStatus status, bool requestedBefore, bool rationale, PermissionStatus expected) =>
        Assert.Equal(expected, AndroidPermissionMapper.Refine(status, requestedBefore, rationale));

    [Theory]
    [InlineData(true, PermissionStatus.NotRequired)]
    [InlineData(false, PermissionStatus.Denied)]
    public void Legacy_notifications_are_denied_only_when_disabled_in_settings(bool enabled, PermissionStatus expected) =>
        Assert.Equal(expected, AndroidPermissionMapper.MapLegacyNotifications(enabled));

    [Theory]
    [InlineData(true, false, PermissionStatus.Granted)]
    [InlineData(true, true, PermissionStatus.Granted)]
    [InlineData(false, false, PermissionStatus.NotDetermined)]
    [InlineData(false, true, PermissionStatus.Denied)]
    public void Exact_alarms_are_undecided_until_requested(bool canSchedule, bool requestedBefore, PermissionStatus expected) =>
        Assert.Equal(expected, AndroidPermissionMapper.MapExactAlarms(canSchedule, requestedBefore));
}

public sealed class PermissionErrorTests
{
    private static readonly CapabilityId s_capability = NativeCapabilities.Camera;

    // Messages thrown by Microsoft.Maui.Essentials (Permissions.android.cs).
    private const string MissingDeclarationMessage = "You need to declare using the permission: `android.permission.CAMERA` in your AndroidManifest.xml";
    private const string WrongThreadMessage = "Permission request must be invoked on main thread.";

    [Theory]
    [InlineData(MissingDeclarationMessage, nameof(EssentialsPermissionFailure.MissingDeclaration))]
    [InlineData("Add it to androidmanifest.XML", nameof(EssentialsPermissionFailure.MissingDeclaration))]
    [InlineData("Please DECLARE the permission", nameof(EssentialsPermissionFailure.MissingDeclaration))]
    [InlineData(WrongThreadMessage, nameof(EssentialsPermissionFailure.WrongThread))]
    [InlineData("Permission denied by the user.", nameof(EssentialsPermissionFailure.Denied))]
    [InlineData("", nameof(EssentialsPermissionFailure.Denied))]
    [InlineData(null, nameof(EssentialsPermissionFailure.Denied))]
    public void Classifies_Essentials_messages(string? message, string expected) =>
        Assert.Equal(Enum.Parse<EssentialsPermissionFailure>(expected), AndroidPermissionErrors.Classify(message));

    [Theory]
    [InlineData(MissingDeclarationMessage, "android.permission.CAMERA")]
    [InlineData("`a` and `b`", "a")]
    [InlineData("empty `` name", null)]
    [InlineData("unterminated `name", null)]
    [InlineData("no name", null)]
    [InlineData(null, null)]
    public void Extracts_the_permission_named_in_backticks(string? message, string? expected) =>
        Assert.Equal(expected, AndroidPermissionErrors.ExtractPermissionName(message));

    [Fact]
    public void An_undeclared_permission_is_unavailable_with_a_missing_declaration()
    {
        var error = AndroidPermissionErrors.MissingDeclaration(s_capability, C.PostNotifications);

        Assert.Equal(NativeResultStatus.Unavailable, error.Status);
        Assert.Equal(NativeErrorCodes.MissingDeclaration, error.Code);
        Assert.Equal(s_capability, error.Capability);
        Assert.Contains("<uses-permission android:name=\"android.permission.POST_NOTIFICATIONS\" />", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undeclared_permission_exception_names_the_permission()
    {
        var exception = new PermissionException(MissingDeclarationMessage);

        var error = AndroidPermissionErrors.Map(s_capability, exception, NativePermissions.Camera);

        Assert.Equal(NativeResultStatus.Unavailable, error.Status);
        Assert.Equal(NativeErrorCodes.MissingDeclaration, error.Code);
        Assert.Contains("android:name=\"android.permission.CAMERA\"", error.Message, StringComparison.Ordinal);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void An_undeclared_permission_without_a_name_keeps_the_message()
    {
        var exception = new PermissionException("Declare it in AndroidManifest.xml");

        var error = AndroidPermissionErrors.Map(s_capability, exception);

        Assert.Equal(NativeErrorCodes.MissingDeclaration, error.Code);
        Assert.Equal(exception.Message, error.Message);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void A_wrong_thread_request_is_a_platform_error()
    {
        var exception = new PermissionException(WrongThreadMessage);

        var error = AndroidPermissionErrors.Map(s_capability, exception);

        Assert.Equal(NativeResultStatus.Failed, error.Status);
        Assert.Equal(NativeErrorCodes.PlatformError, error.Code);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void Other_permission_exceptions_are_denials_of_the_requested_permission()
    {
        var exception = new PermissionException("Permission denied.");

        var error = AndroidPermissionErrors.Map(s_capability, exception, NativePermissions.Photos);

        Assert.Equal(NativeResultStatus.PermissionDenied, error.Status);
        Assert.Equal(NativeErrorCodes.PermissionDenied, error.Code);
        Assert.Equal(NativePermissions.Photos, error.Permission);
        Assert.Equal(PermissionStatus.Denied, error.PermissionStatus);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void Denials_without_a_known_permission_report_unknown() =>
        Assert.Equal(new NativePermission("unknown"), AndroidPermissionErrors.Map(s_capability, new PermissionException("no")).Permission);
}
