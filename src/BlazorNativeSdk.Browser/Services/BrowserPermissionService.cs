using System.Text.Json;
using BlazorNativeSdk.Browser.Interop;
using Microsoft.Extensions.Logging;

namespace BlazorNativeSdk.Browser.Services;

/// <summary>
/// Browser permissions: checks with the Permissions API (<c>navigator.permissions.query</c>) and
/// <c>Notification.permission</c>; requests with <c>getUserMedia</c> (camera, microphone) and
/// <c>Notification.requestPermission</c>.
/// </summary>
internal sealed partial class BrowserPermissionService : IPermissionService
{
    internal const string OpenSettingsUnsupportedReason =
        "Web pages cannot open browser or system settings. Ask the user to change the permission in the site settings " +
        "(the icon next to the address bar).";

    internal const string ClipboardRequestReason =
        "Browsers ask for clipboard-read access only when the page reads the clipboard. Call IClipboardService.GetTextAsync " +
        "from a user gesture instead of requesting the permission up front.";

    private static readonly HashSet<string> s_known = new(StringComparer.Ordinal)
    {
        NativePermissions.Camera.Name,
        NativePermissions.Microphone.Name,
        NativePermissions.Notifications.Name,
        NativePermissions.ClipboardRead.Name,
    };

    private readonly BrowserInterop _interop;
    private readonly ILogger<BrowserPermissionService> _logger;

    public BrowserPermissionService(BrowserInterop interop, ILogger<BrowserPermissionService> logger)
    {
        _interop = interop;
        _logger = logger;
    }

    public CapabilityId Capability => NativeCapabilities.Permissions;

    /// <summary>Returns whether the browser adapter knows a permission.</summary>
    internal static bool IsKnown(NativePermission permission) => permission.Name is not null && s_known.Contains(permission.Name);

    public async ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var probe = await _interop.ProbeAsync(Capability, "permissions", cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            return BrowserInteropExtensions.ToStatus(Capability, probe.Error);
        }

        var status = CapabilityStatus.Available(Capability, BrowserConstants.AdapterName);
        return probe.Value.PermissionsApi
            ? status
            : status with { Reason = "The Permissions API is not available; checks other than notifications report Unknown." };
    }

    public async ValueTask<NativeResult<PermissionStatus>> CheckAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        if (!IsKnown(permission))
        {
            return Unknown(permission);
        }

        var result = await _interop.InvokeAsync(Capability, "permissionQuery", [permission.Name], cancellationToken, permission).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, permission.Name, result.Error.Code);
            return result.Error;
        }

        return BrowserErrorMapper.ToPermissionStatus(AsString(result.Value));
    }

    public async ValueTask<NativeResult<PermissionStatus>> RequestAsync(NativePermission permission, CancellationToken cancellationToken = default)
    {
        if (!IsKnown(permission))
        {
            return Unknown(permission);
        }

        if (permission == NativePermissions.ClipboardRead)
        {
            var current = await CheckAsync(permission, cancellationToken).ConfigureAwait(false);
            return current is { IsSuccess: true, Value: PermissionStatus.Granted or PermissionStatus.Denied }
                ? current
                : NativeError.Unsupported(Capability, ClipboardRequestReason);
        }

        var result = await _interop.InvokeInteractiveAsync(
            Capability, "permissionRequest", BrowserInteropExtensions.NewOperationId(), [permission.Name], cancellationToken, permission).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            LogFailed(_logger, permission.Name, result.Error.Code);
            return result.Error;
        }

        return BrowserErrorMapper.ToPermissionStatus(AsString(result.Value));
    }

    public ValueTask<NativeResult<bool>> ShouldShowRationaleAsync(NativePermission permission, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Success(false));

    public ValueTask<NativeResult> OpenAppSettingsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(NativeResult.Failure(NativeError.Unsupported(Capability, OpenSettingsUnsupportedReason)));

    private NativeError Unknown(NativePermission permission) =>
        NativeError.Unsupported(Capability, $"Browsers have no '{permission.Name}' permission that a page can check or request.");

    private static string? AsString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    [LoggerMessage(EventId = 3701, Level = LogLevel.Debug, Message = "Browser permission {Permission} failed: {Code}.")]
    private static partial void LogFailed(ILogger logger, string permission, string code);
}
