namespace Earshot.Contracts;

public sealed record AudioEndpoint(
    string EndpointId,          // opaque MMDevice id; unstable across driver reinstall
    EndpointFlow Flow,
    EndpointState State,
    string? FriendlyName,       // may be null: GetValue can fail 0xE000020B on NOTPRESENT
    Guid ContainerId);          // PKEY_Device_ContainerId

public sealed record DeviceModel(
    Guid ContainerId,
    string DisplayName,         // best readable name in the group; U+2019 preserved
    ConnectionState Connection,
    IReadOnlyList<AudioEndpoint> Endpoints);

// TakenUtc and Sequence describe the enumeration Target and AllGroups were built from. A rebuild after a
// settings change, and a failed enumeration, keep both. Check ReadStatus before acting on Target: only an Ok
// snapshot is an observation, and a null Target means "not found" only when Resolution is NotFound or
// PinnedAbsent.
public sealed record DeviceSnapshot(
    DeviceModel? Target,        // resolved target device, or null when not found
    IReadOnlyList<DeviceModel> AllGroups,
    DateTimeOffset TakenUtc)
{
    // Monotonic number of the successful enumeration this snapshot was built from, set by the device monitor
    // on the audio worker. 0 means unknown: nothing was enumerated, or the snapshot was not built by a monitor.
    public long Sequence { get; init; }

    public SnapshotReadStatus ReadStatus { get; init; } = SnapshotReadStatus.NotStarted;

    public TargetResolution Resolution { get; init; } = TargetResolution.None;
}

public sealed class DeviceSnapshotEventArgs(DeviceSnapshot snapshot) : EventArgs
{
    public DeviceSnapshot Snapshot { get; } = snapshot;
}

// A single Bluetooth devnode as seen by the read-only CfgMgr32 verifier.
public sealed record BluetoothNode(
    string InstanceId,
    string EnumeratorPrefix,    // "BTHENUM", "BTHHFENUM", "SWD", ...
    string? Name,
    bool IsPresent,
    NodeBlockStatus Status,
    uint ProblemCode,
    bool ConfigFlagsDisabledBit);

public sealed record BootBlockStatus(
    BlockState State,
    Guid TargetContainerId,
    IReadOnlyList<BluetoothNode> Nodes,
    bool TasksInstalled,
    bool BlockAtBoot)
{
    // False when the setting was not read (config.json missing after setup, not valid or unreadable). BlockAtBoot is
    // then false, since the gate's boot verb refuses on such a file too, but it is not a choice anyone made: nothing
    // is decided from it, and the state is read again.
    public bool BlockAtBootKnown { get; init; } = true;

    // False when a task could not be read (Task Scheduler failed to open it, or to return its security descriptor or
    // XML). The state is then Unknown, not NotSetUp: one failed read is not taken as "not set up", so setup is not
    // offered, and the state is read again.
    public bool TasksKnown { get; init; } = true;
}

public sealed record AudioProtectionSnapshot(
    AudioProtectionState State,
    bool HandsfreeInstalled,    // 0000111E present == not protected
    bool HeadsetInstalled);     // 00001108 (these AirPods never advertise it)

// A battery figure, or none. Percent is null when there is no measured value, so no caller can show a
// number that was never read.
public readonly record struct BatteryReading(int? Percent)
{
    public static BatteryReading None => new(Percent: null);

    public bool HasValue => Percent is not null;
}
