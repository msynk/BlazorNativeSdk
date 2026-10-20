namespace BlazorNativeSdk;

/// <summary>Convenience methods for <see cref="IPermissionService"/>.</summary>
public static class PermissionServiceExtensions
{
    /// <summary>
    /// Checks a permission and requests it when it has not been decided yet. Succeeds when the permission is
    /// <see cref="PermissionStatus.Granted"/>, <see cref="PermissionStatus.Limited"/> or <see cref="PermissionStatus.NotRequired"/>.
    /// </summary>
    /// <returns>
    /// Success, or <see cref="NativeResultStatus.PermissionDenied"/> with the observed <see cref="PermissionStatus"/>,
    /// or the failure reported by the platform.
    /// </returns>
    public static async ValueTask<NativeResult<PermissionStatus>> EnsureAsync(
        this IPermissionService permissions,
        NativePermission permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var check = await permissions.CheckAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!check.IsSuccess)
        {
            return check;
        }

        if (IsGranted(check.Value))
        {
            return check;
        }

        if (check.Value is PermissionStatus.Restricted)
        {
            return NativeError.PermissionDenied(permissions.Capability, permission, check.Value);
        }

        var request = await permissions.RequestAsync(permission, cancellationToken).ConfigureAwait(false);
        if (!request.IsSuccess)
        {
            return request;
        }

        return IsGranted(request.Value)
            ? request
            : NativeError.PermissionDenied(permissions.Capability, permission, request.Value);
    }

    /// <summary>Returns <see langword="true"/> when the status allows the operation to proceed.</summary>
    public static bool IsGranted(this PermissionStatus status) =>
        status is PermissionStatus.Granted or PermissionStatus.Limited or PermissionStatus.NotRequired;
}
