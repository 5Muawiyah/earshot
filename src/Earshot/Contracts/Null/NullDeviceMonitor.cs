namespace Earshot.Contracts.Null;

// Stands in until discovery is wired. Reports that nothing was read (NotStarted, None), never an
// observation, and never raises SnapshotChanged.
public sealed class NullDeviceMonitor : IDeviceMonitor
{
    public DeviceSnapshot Current { get; } =
        new(Target: null, AllGroups: Array.Empty<DeviceModel>(), TakenUtc: DateTimeOffset.UtcNow)
        {
            Sequence = 0,
            ReadStatus = SnapshotReadStatus.NotStarted,
            Resolution = TargetResolution.None,
        };

    // Nothing ever changes, so handlers are not kept.
    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public void Dispose()
    {
    }
}
