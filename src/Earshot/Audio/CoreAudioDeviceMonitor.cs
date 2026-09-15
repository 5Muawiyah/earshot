using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Audio;

// One evaluation of the audio devices: the snapshot it produced (or kept), whether the enumeration
// worked, what was read, every step that failed, and how the target was chosen.
internal sealed record MonitorRefresh(
    DeviceSnapshot Snapshot,
    bool EnumerationOk,
    IReadOnlyList<EndpointReading> Readings,
    IReadOnlyList<StepOutcome> Steps,
    TargetResolution Resolution);

// IDeviceMonitor over Core Audio. Watches endpoint notifications and keeps the device snapshot current.
//
//   Start          registers the notification client on the audio worker, then enumerates, so no change
//                  between the two can be missed. Also listens for settings changes.
//   notifications  the callback only queues a coalesced refresh (see OnNotification). A burst (render and
//                  capture change together; Protect audio quality removes and re-adds the capture
//                  endpoint) becomes one enumeration CoalesceWindow after its first notification.
//   RefreshAsync   enumerates now and returns the snapshot. It works without Start.
//   settings       a change of DeviceMatch or PinnedContainerId chooses the target again from the last
//                  endpoints read, without enumerating.
//   SnapshotChanged is raised through the UI post only when the snapshot changed materially (TakenUtc is
//                  ignored). Current is updated after every successful enumeration.
//
// Every enumeration, model build and publication runs on the audio worker, so they are serialised and
// happen in order. A failed enumeration keeps the previous snapshot, is logged as an error and is reported
// in the MonitorRefresh steps; per-endpoint property failures (0xE000020B on NOTPRESENT endpoints) are
// logged at Debug once each, when first seen.
internal sealed class CoreAudioDeviceMonitor : IDeviceMonitor
{
    internal static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromMilliseconds(150);

    private readonly IAudioWorker _worker;
    private readonly IEndpointSource _source;
    private readonly ISettingsStore _settings;
    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly TimeSpan _coalesceWindow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;

    private DeviceSnapshot _current;
    private int _started;
    private int _disposed;
    private int _refreshPending;
    private long _notificationCount;
    private long _enumerationCount;

    // Audio worker thread only.
    private IReadOnlyList<EndpointReading>? _lastReadings;
    private IReadOnlyList<StepOutcome> _lastSteps = Array.Empty<StepOutcome>();
    private string? _appliedMatch;
    private Guid _appliedPinned;
    private HashSet<string> _reportedFailures = new(StringComparer.Ordinal);

    public CoreAudioDeviceMonitor(IAudioWorker worker, IEndpointSource source, ISettingsStore settings, ILog log, Action<Action> uiPost)
        : this(worker, source, settings, log, uiPost, DefaultCoalesceWindow, static window => Task.Delay(window), static () => DateTimeOffset.UtcNow)
    {
    }

    internal CoreAudioDeviceMonitor(
        IAudioWorker worker,
        IEndpointSource source,
        ISettingsStore settings,
        ILog log,
        Action<Action> uiPost,
        TimeSpan coalesceWindow,
        Func<TimeSpan, Task> delay,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentOutOfRangeException.ThrowIfLessThan(coalesceWindow, TimeSpan.Zero);

        _worker = worker;
        _source = source;
        _settings = settings;
        _log = log;
        _uiPost = uiPost;
        _coalesceWindow = coalesceWindow;
        _delay = delay;
        _utcNow = utcNow;
        _current = new DeviceSnapshot(Target: null, AllGroups: Array.Empty<DeviceModel>(), TakenUtc: utcNow());
    }

    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged;

    public DeviceSnapshot Current => Volatile.Read(ref _current);

    internal TimeSpan CoalesceWindow => _coalesceWindow;

    // Notifications that asked for a refresh, and enumerations run, since construction.
    internal long NotificationCount => Interlocked.Read(ref _notificationCount);

    internal long EnumerationCount => Interlocked.Read(ref _enumerationCount);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _settings.Changed += OnSettingsChanged;
        Observe(_worker.RunAsync(_ => StartOnWorker()), "start watching audio devices");
    }

    public async Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default) =>
        (await RefreshDetailedAsync(ct).ConfigureAwait(false)).Snapshot;

    // Enumerates now, on the worker, and returns everything the evaluation saw.
    internal Task<MonitorRefresh> RefreshDetailedAsync(CancellationToken ct = default)
    {
        if (IsDisposed)
        {
            return Task.FromException<MonitorRefresh>(new ObjectDisposedException(nameof(CoreAudioDeviceMonitor)));
        }

        return _worker.RunAsync(_ => EvaluateOnWorker("refresh", subscribeStep: null), ct);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }

        _settings.Changed -= OnSettingsChanged;

        // Unregistering needs the worker. It is queued rather than waited for, so disposing never blocks
        // the caller; the worker also unregisters a client that is still registered when it stops.
        Observe(_worker.RunAsync(_ => StopOnWorker()), "stop watching audio devices");
    }

    // Called by the notification client on MMDevAPI's callback thread, whose identity is undocumented.
    // It must not block: no locks, no waits, no COM calls. It only counts, and queues one coalesced refresh
    // to the thread pool if none is pending. The pending flag is cleared on the worker just before the
    // enumeration starts, so a notification that arrives later always gets a pass of its own.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
    internal void OnNotification(EndpointNotification notification)
    {
        if (!notification.RequiresRefresh || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _notificationCount);
        if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static monitor => monitor.BeginCoalescedRefresh(), this, preferLocal: false);
    }

    private void BeginCoalescedRefresh() => Observe(CoalescedRefreshAsync(), "refresh after a device notification");

    private async Task CoalescedRefreshAsync()
    {
        bool ran = false;
        try
        {
            await _delay(_coalesceWindow).ConfigureAwait(false);
            if (IsDisposed)
            {
                return;
            }

            await _worker.RunAsync(_ =>
            {
                ran = true;
                Volatile.Write(ref _refreshPending, 0);
                return EvaluateOnWorker("notification", subscribeStep: null);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!ran)
            {
                Volatile.Write(ref _refreshPending, 0);
            }
        }
    }

    private void OnSettingsChanged(object? sender, EarshotSettings settings)
    {
        if (IsDisposed)
        {
            return;
        }

        Observe(_worker.RunAsync(_ => ReapplySettingsOnWorker()), "apply a settings change to the audio devices");
    }

    private MonitorRefresh StartOnWorker()
    {
        if (IsDisposed)
        {
            return new MonitorRefresh(Current, false, Array.Empty<EndpointReading>(), Array.Empty<StepOutcome>(), TargetResolution.None);
        }

        StepOutcome subscribe = _source.Subscribe(OnNotification);
        if (subscribe.Ok)
        {
            _log.Info("Watching audio devices for changes.");
        }
        else
        {
            _log.Error("Could not watch audio devices for changes: " + Describe(subscribe) +
                       ". The device state updates only when it is refreshed.");
        }

        return EvaluateOnWorker("start", subscribe);
    }

    private StepOutcome StopOnWorker()
    {
        StepOutcome step = _source.Unsubscribe();
        if (step.Ok)
        {
            _log.Info("Stopped watching audio devices.");
        }
        else if (step.Code == NativeCodes.NotAttempted)
        {
            _log.Write(LogLevel.Debug, "Stop watching audio devices: " + Describe(step));
        }
        else
        {
            _log.Error("Could not stop watching audio devices: " + Describe(step));
        }

        ReportCallbackFailures();
        return step;
    }

    private MonitorRefresh EvaluateOnWorker(string reason, StepOutcome? subscribeStep)
    {
        Interlocked.Increment(ref _enumerationCount);
        ReportCallbackFailures();

        EndpointEnumeration enumeration = _source.Enumerate();
        var steps = new List<StepOutcome>();
        if (subscribeStep is { Ok: false })
        {
            steps.Add(subscribeStep);
        }

        steps.AddRange(enumeration.Steps);
        ReportSteps(enumeration, reason);

        if (!enumeration.Ok)
        {
            _lastSteps = steps;
            return new MonitorRefresh(Current, false, _lastReadings ?? Array.Empty<EndpointReading>(), steps, TargetResolution.None);
        }

        _lastReadings = enumeration.Readings;
        return PublishOnWorker(enumeration.Readings, steps, reason);
    }

    private MonitorRefresh? ReapplySettingsOnWorker()
    {
        if (IsDisposed || _lastReadings is null)
        {
            return null;
        }

        EarshotSettings settings = _settings.Current;
        if (string.Equals(settings.DeviceMatch, _appliedMatch, StringComparison.Ordinal) && settings.PinnedContainerId == _appliedPinned)
        {
            return null;
        }

        return PublishOnWorker(_lastReadings, _lastSteps, "settings");
    }

    private MonitorRefresh PublishOnWorker(IReadOnlyList<EndpointReading> readings, IReadOnlyList<StepOutcome> steps, string reason)
    {
        EarshotSettings settings = _settings.Current;
        EndpointModel model = EndpointModelBuilder.Build(readings, settings.DeviceMatch, settings.PinnedContainerId, _utcNow());
        _appliedMatch = settings.DeviceMatch;
        _appliedPinned = settings.PinnedContainerId;
        _lastSteps = steps;

        DeviceSnapshot previous = Volatile.Read(ref _current);
        DeviceSnapshot snapshot = model.Snapshot;
        Volatile.Write(ref _current, snapshot);

        if (!EndpointModelBuilder.AreEquivalent(previous, snapshot) && !IsDisposed)
        {
            _log.Info("Audio devices changed (" + reason + "): " + Describe(model));
            _uiPost(() => RaiseSnapshotChanged(snapshot));
        }

        return new MonitorRefresh(snapshot, true, readings, steps, model.Resolution);
    }

    private void RaiseSnapshotChanged(DeviceSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));
    }

    private void ReportSteps(EndpointEnumeration enumeration, string reason)
    {
        if (!enumeration.Ok)
        {
            foreach (StepOutcome step in enumeration.Steps)
            {
                _log.Error("Could not read the audio devices (" + reason + "): " + Describe(step) + ". The last known state is kept.");
            }

            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (StepOutcome step in enumeration.Steps)
        {
            if (step.Ok)
            {
                continue;
            }

            string key = step.Step + "|" + step.CodeName + "|" + step.Detail;
            seen.Add(key);
            if (!_reportedFailures.Contains(key))
            {
                _log.Write(LogLevel.Debug, "Audio endpoint read failed: " + Describe(step));
            }
        }

        _reportedFailures = seen;
    }

    private void ReportCallbackFailures()
    {
        (int count, Exception? last) = _source.TakeCallbackFailures();
        if (count > 0)
        {
            _log.Error(count + " audio device notifications could not be handled; the state may be out of date until the next refresh.", last);
        }
    }

    private void Observe(Task task, string what)
    {
        task.ContinueWith(
            static (t, state) =>
            {
                var (monitor, action) = ((CoreAudioDeviceMonitor, string))state!;
                monitor.ReportBackgroundFailure(t, action);
            },
            (this, what),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void ReportBackgroundFailure(Task task, string what)
    {
        if (task.IsCanceled)
        {
            _log.Write(LogLevel.Debug, "Cancelled: " + what + ".");
            return;
        }

        Exception? error = task.Exception?.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception;
        if (error is ObjectDisposedException && IsDisposed)
        {
            // Shutting down: the worker has stopped, and it unregistered the client itself.
            _log.Write(LogLevel.Debug, "Skipped while shutting down: " + what + ".");
            return;
        }

        _log.Error("Could not " + what + ".", error);
    }

    internal static string Describe(StepOutcome step) =>
        step.Step + " " + step.CodeName + (step.Detail is null ? "" : " (" + step.Detail + ")");

    private static string Describe(EndpointModel model)
    {
        DeviceSnapshot snapshot = model.Snapshot;
        int endpoints = snapshot.AllGroups.Sum(g => g.Endpoints.Count);
        string groups = snapshot.AllGroups.Count + " groups, " + endpoints + " endpoints";
        if (snapshot.Target is not DeviceModel target)
        {
            return "no target device; " + groups + ".";
        }

        return "target " + target.ContainerId.ToString("B", CultureInfo.InvariantCulture) + " \"" + target.DisplayName + "\" " + target.Connection +
               " (" + (model.Resolution == TargetResolution.Pinned ? "pinned" : "matched by name") + "); " + groups + ".";
    }
}
