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

public sealed record DeviceSnapshot(
    DeviceModel? Target,        // resolved target device, or null when not found
    IReadOnlyList<DeviceModel> AllGroups,
    DateTimeOffset TakenUtc);

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
    bool BlockAtBoot);

public sealed record AudioProtectionSnapshot(
    AudioProtectionState State,
    bool HandsfreeInstalled,    // 0000111E present == not protected
    bool HeadsetInstalled);     // 00001108 (these AirPods never advertise it)

public readonly record struct BatteryReading(bool HasValue, int Percent);
