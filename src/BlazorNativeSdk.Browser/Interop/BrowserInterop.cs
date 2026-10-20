using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace BlazorNativeSdk.Browser.Interop;

/// <summary>
/// Owns the reference to <c>blazor-native-sdk.js</c> and to the per-scope JavaScript session created from it. The module is
/// imported lazily, once, on first use; a failed import (for example while prerendering) is retried on the next call.
/// </summary>
/// <remarks>
/// Every module function returns <c>{ ok: true, value }</c> or <c>{ ok: false, name, message }</c> instead of throwing, so
/// browser failures are mapped deterministically by <see cref="BrowserErrorMapper"/>. Infrastructure failures (prerendering,
/// a disconnected circuit, timeouts) are mapped by <see cref="BrowserErrorMapper.MapInteropException"/>.
/// </remarks>
internal sealed partial class BrowserInterop : IAsyncDisposable, IDisposable
{
    private readonly IJSRuntime _js;
    private readonly BrowserNativeOptions _options;
    private readonly ILogger<BrowserInterop> _logger;
    private readonly DotNetObjectReference<BrowserJsCallbacks> _callbacksReference;
    private readonly Lock _gate = new();
    private Task<IJSObjectReference>? _session;
    private IJSObjectReference? _module;
    private int _disposed;

    public BrowserInterop(IJSRuntime js, IOptions<BrowserNativeOptions> options, ILogger<BrowserInterop> logger)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(options);
        _js = js;
        _options = options.Value;
        _logger = logger;
        Callbacks = new BrowserJsCallbacks(logger);
        _callbacksReference = DotNetObjectReference.Create(Callbacks);
    }

    /// <summary>Events raised by the JavaScript session.</summary>
    public BrowserJsCallbacks Callbacks { get; }

    /// <summary>The adapter options.</summary>
    public BrowserNativeOptions Options => _options;

    /// <summary><see langword="true"/> once the JavaScript session has been created successfully.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _session is { IsCompletedSuccessfully: true };
            }
        }
    }

    /// <summary>Serializes a request for the module.</summary>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Deserializes a successful module result.</summary>
    public static NativeResult<T> Read<T>(CapabilityId capability, NativeResult<JsonElement> result, JsonTypeInfo<T> typeInfo)
    {
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        try
        {
            var value = result.Value.Deserialize(typeInfo);
            return value is null
                ? NativeError.Failed(capability, NativeErrorCodes.PlatformError, "The browser module returned an empty result.")
                : NativeResult.Success(value);
        }
        catch (JsonException ex)
        {
            return NativeError.Failed(capability, NativeErrorCodes.PlatformError, "The browser module returned an unexpected result.", ex);
        }
    }

    /// <summary>Interprets the <c>{ ok, value }</c> / <c>{ ok: false, name, message }</c> envelope.</summary>
    public static NativeResult<JsonElement> ReadEnvelope(CapabilityId capability, JsonElement element, NativePermission? permission = null)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("ok", out var ok))
        {
            if (ok.ValueKind == JsonValueKind.True)
            {
                return element.TryGetProperty("value", out var value) ? value : default(JsonElement);
            }

            if (ok.ValueKind == JsonValueKind.False)
            {
                BrowserJsError? error = null;
                try
                {
                    error = element.Deserialize(BrowserJsonContext.Default.BrowserJsError);
                }
                catch (JsonException)
                {
                }

                return BrowserErrorMapper.Map(capability, error ?? new BrowserJsError { Name = "Error", Message = "The browser reported an unspecified error." }, permission);
            }
        }

        return NativeError.Failed(capability, NativeErrorCodes.PlatformError, "The browser module returned an unexpected response.");
    }

    /// <summary>Calls a short-running session function, bounded by <see cref="BrowserNativeOptions.InteropTimeout"/>.</summary>
    public async ValueTask<NativeResult<JsonElement>> InvokeAsync(
        CapabilityId capability,
        string identifier,
        object?[] args,
        CancellationToken cancellationToken,
        NativePermission? permission = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InteropTimeout);
        try
        {
            var session = await GetSessionAsync(timeout.Token).ConfigureAwait(false);
            var element = await session.InvokeAsync<JsonElement>(identifier, timeout.Token, args).ConfigureAwait(false);
            return ReadEnvelope(capability, element, permission);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(capability, identifier, ex);
        }
    }

    /// <summary>
    /// Calls a session function that shows UI (overlay, picker, share sheet). It is not limited by the interop timeout. The
    /// operation id is passed as the first argument; cancelling <paramref name="cancellationToken"/> aborts the operation in
    /// the browser (closing the overlay and stopping media tracks) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async ValueTask<NativeResult<JsonElement>> InvokeInteractiveAsync(
        CapabilityId capability,
        string identifier,
        string operationId,
        object?[] args,
        CancellationToken cancellationToken,
        NativePermission? permission = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IJSObjectReference session;
        try
        {
            session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(capability, identifier, ex);
        }

        var allArgs = new object?[args.Length + 1];
        allArgs[0] = operationId;
        Array.Copy(args, 0, allArgs, 1, args.Length);

        try
        {
            // No timeout: the user decides how long the UI stays open. Blazor Server applies its default JS call timeout
            // only to calls without a token, so CancellationToken.None disables it here. WaitAsync returns as soon as the
            // caller cancels; the browser side is then aborted explicitly below (not from a token callback, whose
            // registration could be disposed by the inline continuation before it runs).
            var element = await session.InvokeAsync<JsonElement>(identifier, CancellationToken.None, allArgs)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadEnvelope(capability, element, permission);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closes the overlay, stops media tracks and releases files the operation produced.
            Abort(operationId);
            throw;
        }
        catch (Exception ex)
        {
            return MapFailure(capability, identifier, ex);
        }
    }

    /// <summary>Aborts an interactive operation and releases files it produced that were not taken. Fire and forget.</summary>
    public void Abort(string operationId)
    {
        if (TryGetConnectedSession() is { } session)
        {
            FireAndForget(session.InvokeVoidAsync("abort", operationId), "abort");
        }
    }

    /// <summary>Takes ownership of a Blob registered by an interactive operation.</summary>
    public async ValueTask<IJSObjectReference> TakeFileAsync(string handle, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return await session.InvokeAsync<IJSObjectReference>("takeFile", cancellationToken, handle).ConfigureAwait(false);
    }

    /// <summary>Opens a Blob for reading through a JS stream reference.</summary>
    public async ValueTask<Stream> OpenBlobStreamAsync(IJSObjectReference blob, long maxAllowedSize, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var reference = await session.InvokeAsync<IJSStreamReference>("blobStream", cancellationToken, blob).ConfigureAwait(false);
        try
        {
            if (reference.Length > maxAllowedSize)
            {
                throw new IOException($"The file ({reference.Length} bytes) exceeds the maximum allowed size of {maxAllowedSize} bytes.");
            }

            var stream = await reference.OpenReadStreamAsync(maxAllowedSize, cancellationToken).ConfigureAwait(false);
            return new OwningStream(stream, reference);
        }
        catch
        {
            await reference.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gets the JavaScript session, importing the module on first use.</summary>
    public async ValueTask<IJSObjectReference> GetSessionAsync(CancellationToken cancellationToken)
    {
        Task<IJSObjectReference> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_session is null || _session.IsFaulted || _session.IsCanceled)
            {
                _session = CreateSessionAsync();
            }

            task = _session;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var (session, module) = Detach();
        try
        {
            if (session is not null)
            {
                await session.InvokeVoidAsync("dispose").ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or InvalidOperationException or JSException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser is gone or never attached: nothing to clean up on the JavaScript side.
        }
        finally
        {
            _callbacksReference.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var (session, module) = Detach();
        if (session is not null)
        {
            FireAndForget(session.InvokeVoidAsync("dispose"), "dispose");
            FireAndForget(session.DisposeAsync(), "dispose");
        }

        if (module is not null)
        {
            FireAndForget(module.DisposeAsync(), "dispose");
        }

        _callbacksReference.Dispose();
    }

    internal static bool IsHostNotReady(NativeError error) => error.Code == NativeErrorCodes.HostNotReady;

    private (IJSObjectReference? Session, IJSObjectReference? Module) Detach()
    {
        lock (_gate)
        {
            var session = _session is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
            var module = _module;
            _session = null;
            _module = null;
            return (session, module);
        }
    }

    private IJSObjectReference? TryGetConnectedSession()
    {
        lock (_gate)
        {
            return _disposed == 0 && _session is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
        }
    }

    private async Task<IJSObjectReference> CreateSessionAsync()
    {
        IJSObjectReference? module = null;
        try
        {
            module = await _js.InvokeAsync<IJSObjectReference>("import", _options.ModulePath).ConfigureAwait(false);
            var sessionOptions = Serialize(CreateSessionOptions(_options), BrowserJsonContext.Default.SessionOptionsDto);
            var session = await module.InvokeAsync<IJSObjectReference>("createSession", _callbacksReference, sessionOptions).ConfigureAwait(false);

            bool disposed;
            lock (_gate)
            {
                disposed = _disposed != 0;
                if (!disposed)
                {
                    _module = module;
                }
            }

            if (disposed)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                await module.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(BrowserInterop));
            }

            LogSessionCreated(_logger);
            return session;
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException)
            {
                LogNotReady(_logger);
            }
            else
            {
                LogSessionFailed(_logger, ex);
            }

            if (module is not null && !ReferenceEquals(module, _module))
            {
                FireAndForget(module.DisposeAsync(), "import");
            }

            throw;
        }
    }

    private static SessionOptionsDto CreateSessionOptions(BrowserNativeOptions options) => new()
    {
        ProtocolParameter = options.ProtocolHandlerQueryParameter,
        NotificationParameter = BrowserConstants.NotificationClickQueryParameter,
        UseServiceWorkerNotifications = options.UseServiceWorkerNotifications,
        Text = new OverlayTextDto
        {
            CameraTitle = options.OverlayText.CameraTitle,
            ScannerTitle = options.OverlayText.ScannerTitle,
            ScannerPrompt = options.OverlayText.ScannerPrompt,
            Capture = options.OverlayText.Capture,
            StartRecording = options.OverlayText.StartRecording,
            StopRecording = options.OverlayText.StopRecording,
            Cancel = options.OverlayText.Cancel,
            SwitchCamera = options.OverlayText.SwitchCamera,
        },
    };

    private NativeError MapFailure(CapabilityId capability, string identifier, Exception exception)
    {
        if (exception is ObjectDisposedException)
        {
            return NativeError.Unavailable(capability, NativeErrorCodes.HostNotReady, "The browser services for this scope have been disposed.");
        }

        var error = BrowserErrorMapper.MapInteropException(capability, exception);
        LogInteropFailed(_logger, identifier, error.Code);
        return error;
    }

    private void FireAndForget(ValueTask task, string operation)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveAsync(task, operation);

        async Task ObserveAsync(ValueTask pending, string name)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or InvalidOperationException or JSException or OperationCanceledException or ObjectDisposedException)
            {
                LogCleanupFailed(_logger, name);
            }
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug, Message = "Browser module session created.")]
    private static partial void LogSessionCreated(ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug, Message = "JavaScript interop is not available yet (prerendering or static rendering); the browser module will be loaded on the next call.")]
    private static partial void LogNotReady(ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Loading the browser module failed; it will be retried on the next call.")]
    private static partial void LogSessionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Browser call {Function} failed with {Code}.")]
    private static partial void LogInteropFailed(ILogger logger, string function, string code);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Trace, Message = "Browser cleanup call {Operation} could not complete because the browser is not reachable.")]
    private static partial void LogCleanupFailed(ILogger logger, string operation);
}
