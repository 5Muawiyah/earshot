using Earshot.Contracts;

namespace Earshot.Streaming;

// Every record here carries a StepOutcome, the one outcome type the rest of Earshot already uses, rather than a
// second family of HRESULT and detail fields beside it. Step.Code is the raw HRESULT Windows or the exception
// gave (0 on success), or NativeCodes.NotAttempted when no call was made at all, so a refusal never reads as
// S_OK. Step.Detail is a short fixed word or an exception type name, never a message from Windows and never a
// device id.

// DeviceId is an opaque interface path from Windows. Never parse it, never show it, never log it: it carries
// hardware identifiers. StreamingLog.Key gives the short hash the log and the settings file use instead.
// ContainerId is System.Devices.ContainerId as Windows reported it, or Guid.Empty when it reported none; it is
// how the device Earshot manages is recognised without reading anything out of the id.
internal sealed record StreamingDevice(string DeviceId, string DisplayName, Guid ContainerId);

internal sealed record StreamingSupportCheck(StreamingSupport Support, StepOutcome Step);

internal sealed record StreamingDiscovery(
    StreamingDiscoveryStatus Status,
    IReadOnlyList<StreamingDevice> Devices,
    StepOutcome Step);

internal sealed record StreamingEnableOutcome(StreamingEnableStatus Status, string DeviceId, StepOutcome Step);

internal sealed record StreamingOpenOutcome(StreamingOpenStatus Status, string DeviceId, StepOutcome Step);

// Released is false when there was nothing to release.
internal sealed record StreamingReleaseOutcome(bool Released, string DeviceId, StepOutcome Step);

internal sealed record StreamingLinkChanged(string DeviceId, StreamingLinkState State);

// The fixed words a Step.Detail carries when no exception supplied one.
internal static class StreamingDetail
{
    public const string Support = "support";
    public const string Excluded = "excluded";
    public const string UnknownDevice = "unknown device";
    public const string LocalTimeout = "local timeout";
    public const string Cancelled = "cancelled";
    public const string Closed = "closed";
    public const string NothingOpen = "nothing open";
    public const string NothingEnabled = "nothing enabled";
    public const string NotEnabled = "not enabled";
    public const string ReleasedMeanwhile = "released meanwhile";
    public const string NoExtendedError = "no extended error";
    public const string AlreadyOpen = "already open";
}
