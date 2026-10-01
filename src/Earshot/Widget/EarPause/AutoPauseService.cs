using Earshot.App;
using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Wires IWidgetStatus.ReadingApplied into AutoPause.ApplyAsync: every reading of the chosen set is fed the two
// in-ear bits, when the reading was taken and the render facts AutoPause needs (Where, the default render
// endpoint's container, the container this build watches). It also tells AutoPause when the AirPods stop being this
// PC's output, so a pause it remembers is forgotten. See AutoPause's own header for what it decides with that feed;
// this class only wires it, it decides nothing about pausing or resuming itself. It owns the AutoPause it is given
// and disposes it with itself.
internal sealed class AutoPauseService : IDisposable
{
    private readonly IWidgetStatus _status;
    private readonly IDeviceMonitor _deviceMonitor;
    private readonly Func<BootBlockStatus?> _blockStatus;
    private readonly ISettingsStore _settings;
    private readonly AutoPause _autoPause;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AutoPauseService(
        IWidgetStatus status,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ISettingsStore settings,
        AutoPause autoPause,
        TimeProvider time,
        ILog log)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(deviceMonitor);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(autoPause);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _status = status;
        _deviceMonitor = deviceMonitor;
        _blockStatus = blockStatus;
        _settings = settings;
        _autoPause = autoPause;
        _time = time;
        _log = log;

        _status.ReadingApplied += OnReadingApplied;
        _deviceMonitor.SnapshotChanged += OnSnapshotChanged;
    }

    // Raised on the UI sync context. A remembered pause is only resumable while the AirPods are still this PC's output:
    // the watched container's render endpoint is active and the default render endpoint belongs to it. It is read from
    // the snapshot itself, not from the widget's Where, which may not have caught up with this change yet.
    private void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        try
        {
            Guid watched = CoordinatorRules.WatchedContainer(_blockStatus(), _settings.Current, e.Snapshot);
            bool renders = watched != Guid.Empty
                && CoordinatorRules.RenderOf(e.Snapshot, watched) == RenderState.Active
                && e.Snapshot.DefaultRenderContainerId == watched;
            _autoPause.NoteOutput(renders);
        }
        catch (Exception ex)
        {
            _log.Error("Auto-pause: reading the output change threw.", ex);
        }
    }

    // Runs on the UI thread (ReadingApplied is documented as raised there). Kicks off the async work
    // and returns at once, the same way TrayContext's own tray-icon and menu handlers do
    // (_ = _coordinator.RefreshStatusAsync(); Launch/RunReportedAsync): the discarded task is an
    // async Task method that wraps its own body in try/catch, so nothing here is an unobserved-task crash
    // waiting to happen.
    private void OnReadingApplied(object? sender, ReadingAppliedEventArgs e) => _ = RunAsync(e);

    // The media-session boundary (Windows Media Controls, reached through IMediaSessions/AutoPause): no
    // silent catch, every exception is logged before it is swallowed, so a Windows Media Controls failure
    // can never crash the process or take the event handler down with it.
    private async Task RunAsync(ReadingAppliedEventArgs e)
    {
        CancellationToken ct = _lifetime.Token;
        try
        {
            // WidgetStatusService applies a reading's state (the in-ear bits, Where, the time of the last
            // reading) under its own lock before it ever posts ReadingApplied (ApplyMessage locks, mutates,
            // unlocks, then posts); PublishAndNotify, which only reads that already-updated state to build the
            // snapshot it caches, runs synchronously straight after, still ahead of this posted delegate
            // ever running. So by the time this handler is invoked, Current already reflects at least this
            // reading. It could in principle reflect a still newer one if another advertisement was parsed
            // and posted in the meantime, but BLE advertising intervals are far longer than one UI message
            // pump turn, and a UI post queue is FIFO, so that race is not exercised here.
            WidgetSnapshot current = _status.Current;
            DeviceSnapshot device = _deviceMonitor.Current;
            Guid watchedContainerId = CoordinatorRules.WatchedContainer(_blockStatus(), _settings.Current, device);

            await _autoPause.ApplyAsync(
                e.Reading.Left.InEar,
                e.Reading.Right.InEar,
                e.At,
                _time.GetUtcNow(),
                current.Where,
                device.DefaultRenderContainerId,
                watchedContainerId,
                ct,
                e.SelectionGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed mid-call: expected, not an error.
        }
        catch (Exception ex)
        {
            _log.Error("Auto-pause: ApplyAsync threw.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _status.ReadingApplied -= OnReadingApplied;
        _deviceMonitor.SnapshotChanged -= OnSnapshotChanged;
        _autoPause.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
