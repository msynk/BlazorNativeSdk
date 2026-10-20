using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Biometric;
using AndroidX.Fragment.App;
using BlazorNativeSdk.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace BlazorNativeSdk.AndroidPlatform;

/// <summary>
/// <see cref="IBiometricService"/> for Android based on AndroidX <see cref="BiometricPrompt"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The prompt is shown on the current <see cref="FragmentActivity"/> (MAUI's <c>MauiAppCompatActivity</c> is one).</item>
/// <item><see cref="BiometricRequest.RequireStrongBiometric"/> selects <c>BIOMETRIC_STRONG</c> (Class 3), otherwise
/// <c>BIOMETRIC_WEAK</c> (Class 2). With <see cref="BiometricRequest.AllowDeviceCredential"/> the PIN/pattern/password is
/// offered instead of a cancel button, except on Android 9–10 with strong biometrics, where AndroidX cannot combine both
/// and the credential fallback is dropped.</item>
/// <item>A rejected finger or face is a soft failure: the prompt stays open and the user can retry.</item>
/// <item>Android reports "biometric" or "device credential" but not which modality was used;
/// <see cref="BiometricAuthentication.Method"/> names the modality only when the device has exactly one.</item>
/// <item><see cref="BiometricRequest.FallbackLabel"/> is iOS-only and ignored.</item>
/// </list>
/// </remarks>
public sealed partial class AndroidBiometricService : IBiometricService
{
    private const string AdapterName = "android";
    private readonly ILogger<AndroidBiometricService> _logger;

    /// <summary>Creates the service.</summary>
    public AndroidBiometricService(ILogger<AndroidBiometricService> logger) => _logger = logger;

    /// <inheritdoc />
    public CapabilityId Capability => NativeCapabilities.Biometrics;

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (info, weakResult) = Probe();
        if (info.AvailableKinds != BiometricKinds.None)
        {
            var features = new List<string>();
            AddFeature(features, info.AvailableKinds, BiometricKinds.Fingerprint, BiometricFeatures.Fingerprint);
            AddFeature(features, info.AvailableKinds, BiometricKinds.Face, BiometricFeatures.Face);
            AddFeature(features, info.AvailableKinds, BiometricKinds.Iris, BiometricFeatures.Iris);
            AddFeature(features, info.AvailableKinds, BiometricKinds.DeviceCredential, BiometricFeatures.DeviceCredential);
            return ValueTask.FromResult(CapabilityStatus.Available(Capability, AdapterName, features));
        }

        return ValueTask.FromResult(CapabilityStatus.FromError(AndroidBiometricErrorMapper.MapCanAuthenticate(weakResult), AdapterName));
    }

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricInfo>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        NativeOperation.RunAsync<BiometricInfo>(Capability, _ => ValueTask.FromResult<NativeResult<BiometricInfo>>(Probe().Info), cancellationToken);

    /// <inheritdoc />
    public ValueTask<NativeResult<BiometricAuthentication>> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NativeOperation.RunAsync<BiometricAuthentication>(Capability, async ct =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return NativeError.Failed(Capability, NativeErrorCodes.InvalidArgument, "BiometricRequest.Reason is required.");
            }

            var (authenticators, credentialAllowed) = AndroidBiometricErrorMapper.ResolveAuthenticators(
                AndroidContext.ApiLevel, request.RequireStrongBiometric, request.AllowDeviceCredential);
            if (request.AllowDeviceCredential && !credentialAllowed)
            {
                LogCredentialFallbackDropped(_logger);
            }

            using (var manager = BiometricManager.From(AndroidContext.App))
            {
                var can = manager.CanAuthenticate(authenticators);
                if (can != AndroidBiometricErrorMapper.Success)
                {
                    return AndroidBiometricErrorMapper.MapCanAuthenticate(can);
                }
            }

            var hardware = GetHardwareKinds();
            var completion = new TaskCompletionSource<NativeResult<BiometricAuthentication>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = await MainThread.InvokeOnMainThreadAsync(
                () => PromptSession.Start(request, authenticators, credentialAllowed, hardware, completion, _logger)).ConfigureAwait(false);
            if (!session.IsSuccess)
            {
                return session.Error;
            }

            var completed = false;
            try
            {
                var result = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
                completed = true;
                return result;
            }
            finally
            {
                var cancel = !completed;
                await MainThread.InvokeOnMainThreadAsync(() => session.Value.Close(cancel)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private static (BiometricInfo Info, int WeakResult) Probe()
    {
        int weak;
        int strong;
        using (var manager = BiometricManager.From(AndroidContext.App))
        {
            weak = manager.CanAuthenticate(AndroidBiometricErrorMapper.BiometricWeak);
            strong = manager.CanAuthenticate(AndroidBiometricErrorMapper.BiometricStrong);
        }

        var deviceSecure = AndroidContext.App.GetSystemService(Context.KeyguardService) is KeyguardManager keyguard && keyguard.IsDeviceSecure;
        return (AndroidBiometricErrorMapper.CreateInfo(weak, strong, GetHardwareKinds(), deviceSecure), weak);
    }

    private static BiometricKinds GetHardwareKinds()
    {
        var packages = AndroidContext.App.PackageManager;
        if (packages is null)
        {
            return BiometricKinds.None;
        }

        var kinds = BiometricKinds.None;
        if (packages.HasSystemFeature(PackageManager.FeatureFingerprint))
        {
            kinds |= BiometricKinds.Fingerprint;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            if (packages.HasSystemFeature(PackageManager.FeatureFace))
            {
                kinds |= BiometricKinds.Face;
            }

            if (packages.HasSystemFeature(PackageManager.FeatureIris))
            {
                kinds |= BiometricKinds.Iris;
            }
        }

        return kinds;
    }

    private static void AddFeature(List<string> features, BiometricKinds available, BiometricKinds kind, string feature)
    {
        if ((available & kind) != 0)
        {
            features.Add(feature);
        }
    }

    [LoggerMessage(EventId = 3801, Level = LogLevel.Debug,
        Message = "Strong biometrics cannot be combined with the device credential on Android 9-10; the credential fallback was dropped.")]
    private static partial void LogCredentialFallbackDropped(ILogger logger);

    [LoggerMessage(EventId = 3802, Level = LogLevel.Debug, Message = "Biometric not recognized; the prompt stays open for another attempt.")]
    private static partial void LogSoftFailure(ILogger logger);

    /// <summary>One prompt session. Created, cancelled and closed on the main thread.</summary>
    private sealed class PromptSession
    {
        private readonly BiometricPrompt _prompt;
        private readonly Callback _callback;
        private bool _closed;

        private PromptSession(BiometricPrompt prompt, Callback callback)
        {
            _prompt = prompt;
            _callback = callback;
        }

        public static NativeResult<PromptSession> Start(
            BiometricRequest request,
            int authenticators,
            bool credentialAllowed,
            BiometricKinds hardware,
            TaskCompletionSource<NativeResult<BiometricAuthentication>> completion,
            ILogger logger)
        {
            var capability = NativeCapabilities.Biometrics;
            if (AndroidContext.CurrentActivity is not { } current)
            {
                return AndroidContext.NoActivity(capability);
            }

            if (current is not FragmentActivity activity)
            {
                return NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
                    "BiometricPrompt needs an AndroidX FragmentActivity. Derive MainActivity from MauiAppCompatActivity or AppCompatActivity.");
            }

            if (activity.SupportFragmentManager.IsStateSaved)
            {
                return NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady,
                    "The activity is stopped; show the biometric prompt while the app is in the foreground.");
            }

            var callback = new Callback(completion, hardware, logger);
            var prompt = new BiometricPrompt(activity, callback);
            using var builder = new BiometricPrompt.PromptInfo.Builder();
            builder.SetTitle(string.IsNullOrWhiteSpace(request.Title) ? AppInfo.Current.Name : request.Title);
            builder.SetDescription(request.Reason);
            builder.SetAllowedAuthenticators(authenticators);
            if (!credentialAllowed)
            {
                builder.SetNegativeButtonText(string.IsNullOrWhiteSpace(request.CancelLabel) ? "Cancel" : request.CancelLabel);
            }

            using var promptInfo = builder.Build();
            try
            {
                prompt.Authenticate(promptInfo);
            }
            catch
            {
                prompt.Dispose();
                callback.Detach();
                throw;
            }

            return new PromptSession(prompt, callback);
        }

        public void Close(bool cancel)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (cancel)
            {
                _prompt.CancelAuthentication();
            }

            // The callback stays referenced by the AndroidX view model until the fragment is removed; it is detached rather than
            // disposed so a late call is a harmless no-op.
            _callback.Detach();
            _prompt.Dispose();
        }
    }

    private sealed class Callback : BiometricPrompt.AuthenticationCallback
    {
        private readonly BiometricKinds _hardware;
        private readonly ILogger _logger;
        private TaskCompletionSource<NativeResult<BiometricAuthentication>>? _completion;

        public Callback(TaskCompletionSource<NativeResult<BiometricAuthentication>> completion, BiometricKinds hardware, ILogger logger)
        {
            _completion = completion;
            _hardware = hardware;
            _logger = logger;
        }

        public void Detach() => _completion = null;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            var method = AndroidBiometricErrorMapper.MapMethod(result.AuthenticationType, _hardware);
            Interlocked.Exchange(ref _completion, null)?.TrySetResult(new BiometricAuthentication(method));
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) =>
            Interlocked.Exchange(ref _completion, null)?.TrySetResult(AndroidBiometricErrorMapper.MapPromptError(errorCode, errString?.ToString()));

        public override void OnAuthenticationFailed() => LogSoftFailure(_logger);
    }
}
