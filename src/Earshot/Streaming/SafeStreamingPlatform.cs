using Earshot.Composition;
using Earshot.Contracts;

namespace Earshot.Streaming;

// Safe mode (EARSHOT_SAFE_MODE), for streaming, on the same rule as Composition\SafeDecorators: a member that
// only reads passes through, and a member that would make the radio do anything is refused with NotAttempted,
// says "Safe mode: no device actions." and logs the refusal. The real platform is never called for those.
// EnableAsync tells Windows to accept audio from a device and OpenAsync connects to it, so both are refused.
// Release passes through: letting go of a connection is how the radio is left alone, and in safe mode there
// is never one to let go of. The wrapped platform is held in a private field and exposed to nobody, tests
// included: nothing may reach past a safe-mode decorator (SharedSystemWorkerTests holds the whole of src to that).
internal sealed class SafeStreamingPlatform : IStreamingPlatform
{
    private readonly IStreamingPlatform _inner;
    private readonly ILog _log;

    public SafeStreamingPlatform(IStreamingPlatform inner, ILog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _log = log;
    }

    public event EventHandler<StreamingLinkChanged>? LinkChanged
    {
        add => _inner.LinkChanged += value;
        remove => _inner.LinkChanged -= value;
    }

    public static IStreamingPlatform Wrap(IStreamingPlatform inner, ILog log) =>
        inner as SafeStreamingPlatform ?? new SafeStreamingPlatform(inner, log);

    public StreamingSupportCheck CheckSupport() => _inner.CheckSupport();

    public Task<StreamingDiscovery> ListStreamCapableDevicesAsync(CancellationToken cancellationToken) =>
        _inner.ListStreamCapableDevicesAsync(cancellationToken);

    public Task<StreamingEnableOutcome> EnableAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _log.Warn(SafeDecorators.Message + " Refused: play from a phone (enable).");
        return Task.FromResult(new StreamingEnableOutcome(StreamingEnableStatus.StartFailed, deviceId, SafeDecorators.RefusedStep("streaming-enable")));
    }

    public Task<StreamingOpenOutcome> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _log.Warn(SafeDecorators.Message + " Refused: play from a phone (open).");
        return Task.FromResult(new StreamingOpenOutcome(StreamingOpenStatus.CallFailed, deviceId, SafeDecorators.RefusedStep("streaming-open")));
    }

    public StreamingReleaseOutcome Release(string deviceId) => _inner.Release(deviceId);
}
