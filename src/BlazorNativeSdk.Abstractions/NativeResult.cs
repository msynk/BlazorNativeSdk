using System.Diagnostics.CodeAnalysis;

namespace BlazorNativeSdk;

/// <summary>
/// The outcome of a native operation that produces no value. <c>default(NativeResult)</c> represents success.
/// </summary>
/// <remarks>
/// A <see cref="NativeError"/> converts implicitly to a failed result, so adapters can write
/// <c>return NativeError.Unsupported(NativeCapabilities.Share);</c>.
/// </remarks>
public readonly struct NativeResult : IEquatable<NativeResult>
{
    private readonly NativeError? _error;

    private NativeResult(NativeError? error) => _error = error;

    /// <summary>A successful result.</summary>
    public static NativeResult Ok => default;

    /// <summary>The outcome status.</summary>
    public NativeResultStatus Status => _error?.Status ?? NativeResultStatus.Success;

    /// <summary><see langword="true"/> when the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _error is null;

    /// <summary>The error, when the operation did not succeed.</summary>
    public NativeError? Error => _error;

    /// <summary>Creates a successful result.</summary>
    public static NativeResult Success() => default;

    /// <summary>Creates a successful result that carries <paramref name="value"/>.</summary>
    public static NativeResult<T> Success<T>(T value) => new(value);

    /// <summary>Creates a failed result.</summary>
    public static NativeResult Failure(NativeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new NativeResult(error);
    }

    /// <summary>Creates a failed result of type <typeparamref name="T"/>.</summary>
    public static NativeResult<T> Failure<T>(NativeError error) => new(error);

    /// <summary>Throws a <see cref="NativeCapabilityException"/> when the result is not successful.</summary>
    public void ThrowIfFailed()
    {
        if (_error is not null)
        {
            throw _error.ToException();
        }
    }

    /// <summary>Converts this result to a typed result carrying <paramref name="value"/> when successful.</summary>
    public NativeResult<T> WithValue<T>(T value) => _error is null ? new NativeResult<T>(value) : new NativeResult<T>(_error);

    /// <summary>Invokes one of two functions depending on the outcome.</summary>
    public TResult Match<TResult>(Func<TResult> onSuccess, Func<NativeError, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return _error is null ? onSuccess() : onFailure(_error);
    }

    /// <summary>Creates a failed result from an error.</summary>
    public static implicit operator NativeResult(NativeError error) => Failure(error);

    /// <inheritdoc />
    public bool Equals(NativeResult other) => Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NativeResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _error?.GetHashCode() ?? 0;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(NativeResult left, NativeResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(NativeResult left, NativeResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => _error is null ? "Success" : _error.ToString();
}

/// <summary>
/// The outcome of a native operation that produces a <typeparamref name="T"/> on success.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// Values and errors convert implicitly, so adapters can write <c>return file;</c> or
/// <c>return NativeError.Cancelled(NativeCapabilities.Camera);</c>. A <c>default</c> instance is treated as a
/// failure with code <see cref="NativeErrorCodes.Uninitialized"/>, never as an accidental success.
/// </remarks>
public readonly struct NativeResult<T> : IEquatable<NativeResult<T>>
{
    private readonly T? _value;
    private readonly NativeError? _error;
    private readonly bool _initialized;

    internal NativeResult(T value)
    {
        _value = value;
        _error = null;
        _initialized = true;
    }

    internal NativeResult(NativeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _value = default;
        _error = error;
        _initialized = true;
    }

    /// <summary>The outcome status.</summary>
    public NativeResultStatus Status => Error?.Status ?? NativeResultStatus.Success;

    /// <summary><see langword="true"/> when the operation succeeded and <see cref="Value"/> is set.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
#pragma warning disable CS8775 // Nullable reference types cannot express "Value is non-null when T is non-nullable".
    public bool IsSuccess => _initialized && _error is null;
#pragma warning restore CS8775

    /// <summary>The value when <see cref="IsSuccess"/> is <see langword="true"/>; otherwise <c>default</c>.</summary>
    public T? Value => _value;

    /// <summary>The error when the operation did not succeed.</summary>
    public NativeError? Error => _initialized ? _error : NativeError.Uninitialized;

    /// <summary>Gets the value when successful.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return IsSuccess;
    }

    /// <summary>Gets the value when successful, or <paramref name="defaultValue"/> otherwise.</summary>
    public T GetValueOrDefault(T defaultValue) => IsSuccess ? _value! : defaultValue;

    /// <summary>Gets the value or throws a <see cref="NativeCapabilityException"/> describing the failure.</summary>
    public T GetValueOrThrow()
    {
        if (!IsSuccess)
        {
            throw Error!.ToException();
        }

        return _value!;
    }

    /// <summary>Transforms the value when successful; propagates the error otherwise.</summary>
    public NativeResult<TResult> Map<TResult>(Func<T, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new NativeResult<TResult>(map(_value!)) : new NativeResult<TResult>(Error!);
    }

    /// <summary>Chains another operation that may fail.</summary>
    public NativeResult<TResult> Bind<TResult>(Func<T, NativeResult<TResult>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value!) : new NativeResult<TResult>(Error!);
    }

    /// <summary>Invokes one of two functions depending on the outcome.</summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<NativeError, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error!);
    }

    /// <summary>Discards the value and returns a non-generic result.</summary>
    public NativeResult WithoutValue() => IsSuccess ? NativeResult.Ok : NativeResult.Failure(Error!);

    /// <summary>Creates a successful result.</summary>
    public static implicit operator NativeResult<T>(T value) => new(value);

    /// <summary>Creates a failed result.</summary>
    public static implicit operator NativeResult<T>(NativeError error) => new(error);

    /// <summary>Converts to a non-generic result.</summary>
    public static implicit operator NativeResult(NativeResult<T> result) => result.WithoutValue();

    /// <inheritdoc />
    public bool Equals(NativeResult<T> other) =>
        _initialized == other._initialized &&
        Equals(_error, other._error) &&
        EqualityComparer<T?>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NativeResult<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_initialized, _error, _value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(NativeResult<T> left, NativeResult<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(NativeResult<T> left, NativeResult<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Success({_value})" : Error!.ToString();
}
