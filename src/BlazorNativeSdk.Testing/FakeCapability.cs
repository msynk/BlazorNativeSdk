using System.Collections.Concurrent;

namespace BlazorNativeSdk.Testing;

/// <summary>Base class for fakes: configurable status and a log of calls for assertions.</summary>
public abstract class FakeCapability : INativeCapability
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Creates a fake that reports <see cref="CapabilityAvailability.Available"/>.</summary>
    protected FakeCapability(CapabilityId capability, params string[] features)
    {
        Capability = capability;
        Status = CapabilityStatus.Available(capability, "fake", features);
    }

    /// <inheritdoc />
    public CapabilityId Capability { get; }

    /// <summary>The status returned by <see cref="GetStatusAsync"/>. Set it to simulate unsupported or denied capabilities.</summary>
    public CapabilityStatus Status { get; set; }

    /// <summary>The names of the operations invoked, in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <inheritdoc />
    public ValueTask<CapabilityStatus> GetStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Status);

    /// <summary>Makes the capability report <see cref="CapabilityAvailability.Unsupported"/>.</summary>
    public void SimulateUnsupported(string? reason = null) => Status = CapabilityStatus.Unsupported(Capability, reason, "fake");

    /// <summary>Records a call.</summary>
    protected void Record(string operation) => _calls.Enqueue(operation);

    /// <summary>Returns the unsupported error when <see cref="Status"/> is unsupported.</summary>
    protected NativeError? StatusError() => Status.Availability switch
    {
        CapabilityAvailability.Unsupported => NativeError.Unsupported(Capability, Status.Reason),
        CapabilityAvailability.Unavailable => NativeError.Unavailable(Capability, Status.ReasonCode ?? NativeErrorCodes.HardwareUnavailable, Status.Reason ?? "Unavailable."),
        _ => null,
    };
}
