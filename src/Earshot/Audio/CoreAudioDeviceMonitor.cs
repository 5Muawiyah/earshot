using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Audio;

// One evaluation of the audio devices: the snapshot it produced (or kept), whether the enumeration
// worked, what was read, every step that failed, and how the target was chosen (Snapshot.Resolution).
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
//                  endpoints read, without enumerating. The rebuilt snapshot keeps the Sequence and TakenUtc
//                  of the enumeration it was built from, and the read status of the last enumeration.
//   SnapshotChanged is raised through the UI post only when the snapshot changed materially (TakenUtc and
//                  Sequence are ignored; ReadStatus and Resolution count). Current is updated after every
//                  successful enumeration, and when an enumeration fails after one that did not.
//
// Snapshot fields. Each successful enumeration takes the next Sequence (from 1) and stamps TakenUtc, with
// ReadStatus Ok and the Resolution the builder chose. A failed enumeration keeps Target, AllGroups, Sequence
// and TakenUtc of the last snapshot (the empty placeholder, Sequence 0, when nothing was read) and sets
// ReadStatus Failed and Resolution ReadFailed, so no caller mistakes it for a new observation or for "not
// found". Before the first enumeration Current is the placeholder with ReadStatus NotStarted and Resolution
// None.
//
// The UI post. uiPost is called on the audio worker thread, so it must queue the action and return at once
// (SynchronizationContext.Post, Control.BeginInvoke). A post that waits for the UI thread
// (SynchronizationContext.Send, Control.Invoke) deadlocks as soon as the UI thread waits on RefreshAsync or
// on anything else queued to the worker. The console run modes post inline, so the handlers run on the
// worker thread there and must not block either.
//
// Every enumeration, model build and publication runs on the audio worker, so they are serialised and
// happen in order. A failed enumeration keeps the previous devices, is logged as an error and is reported
// in the MonitorRefresh steps and in the snapshot's ReadStatus. A per-endpoint read failure is logged once,
// when first seen: at Debug for a name read on a NOTPRESENT endpoint (0xE000020B there is expected), at Warn
// for every other one, because an unreadable id, flow, state or container takes the endpoint out of its
// device.
//
// "Not found" or "could not read". A null Target means any of: nothing read yet (NotStarted, None), the
// last enumeration failed (Failed, ReadFailed), the pinned device has no endpoints (PinnedAbsent), or no
// device matched (NotFound). Check ReadStatus and Resolution before showing "not found".
internal sealed class CoreAudioDeviceMonitor : IDeviceMonitor
{
    internal static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromMilliseconds(150);

    private static readonly string[] NameStepPrefixes =
    [
        CoreAudioEndpointReader.FriendlyNameStep + ":",
        CoreAudioEndpointReader.InterfaceNameStep + ":",
    ];

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
    private int _watchFailed;
    private long _notificationCount;
    private long _enumerationCount;

    // Audio worker thread only.
    private IReadOnlyList<EndpointReading>? _lastReadings;
    private long _lastSequence;
    private DateTimeOffset _lastTakenUtc;
    private bool _lastEnumerationFailed;
    private IReadOnlyList<StepOutcome> _lastSteps = Array.Empty<StepOutcome>();
    private string? _appliedMatch;
    private Guid _appliedPinned;
    private HashSet<string> _reportedFailures = new(StringComparer.Ordinal);

    // uiPost must queue the action and never wait for it to run: see the class comment.
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

    // How the last enumeration went: Current.ReadStatus.
    internal SnapshotReadStatus ReadStatus => Current.ReadStatus;

    // How the target in Current was chosen: Current.Resolution.
    internal TargetResolution Resolution => Current.Resolution;

    internal TimeSpan CoalesceWindow => _coalesceWindow;

    // Notifications that asked for a refresh, and enumerations run, since construction.
    internal long NotificationCount => Interlocked.Read(ref _notificationCount);

    internal long EnumerationCount => Interlocked.Read(ref _enumerationCount);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Set on the worker when the notification client could not be registered, before the enumeration that
    // follows publishes, so a handler of that first SnapshotChanged already sees it.
    public bool WatchFailed => Volatile.Read(ref _watchFailed) != 0;

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

    // Called on MMDevAPI's callback thread right after OnNotification threw. The notification it failed to take
    // never queued a refresh, so nothing would log the failure until some later enumeration. Like the sink this
    // must not block: it only queues a work item that logs the counted failures on the worker.
    internal void OnSinkFailed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static monitor => monitor.Observe(monitor._worker.RunAsync(_ => monitor.ReportCallbackFailures()), "report a failed audio device notification"),
            this,
            preferLocal: false);
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
            DeviceSnapshot current = Current;
            return new MonitorRefresh(current, false, Array.Empty<EndpointReading>(), Array.Empty<StepOutcome>(), current.Resolution);
        }

        StepOutcome subscribe = _source.Subscribe(OnNotification, OnSinkFailed);
        if (subscribe.Ok)
        {
            _log.Info("Watching audio devices for changes.");
        }
        else
        {
            Volatile.Write(ref _watchFailed, 1);
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
            _lastEnumerationFailed = true;
            DeviceSnapshot kept = MarkFailedOnWorker();
            return new MonitorRefresh(kept, false, _lastReadings ?? Array.Empty<EndpointReading>(), steps, kept.Resolution);
        }

        _lastEnumerationFailed = false;
        _lastReadings = enumeration.Readings;
        _lastSequence++;
        _lastTakenUtc = _utcNow();
        return PublishOnWorker(enumeration.Readings, steps, reason);
    }

    // Keeps the devices, Sequence and TakenUtc of Current and marks the read as failed. Raised once, when the
    // status changes, not on every failed enumeration in a row.
    private DeviceSnapshot MarkFailedOnWorker()
    {
        DeviceSnapshot previous = Volatile.Read(ref _current);
        if (previous.ReadStatus == SnapshotReadStatus.Failed)
        {
            return previous;
        }

        DeviceSnapshot failed = previous with { ReadStatus = SnapshotReadStatus.Failed, Resolution = TargetResolution.ReadFailed };
        Volatile.Write(ref _current, failed);
        // ReportSteps has already logged the failure as an error.
        if (!IsDisposed)
        {
            _uiPost(() => RaiseSnapshotChanged(failed));
        }

        return failed;
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

        // TakenUtc and Sequence are those of the enumeration that produced readings, also when this is a
        // rebuild after a settings change, so a rebuild never looks like a newer observation.
        EndpointModel model = EndpointModelBuilder.Build(readings, settings.DeviceMatch, settings.PinnedContainerId, _lastTakenUtc);
        _appliedMatch = settings.DeviceMatch;
        _appliedPinned = settings.PinnedContainerId;
        _lastSteps = steps;

        DeviceSnapshot previous = Volatile.Read(ref _current);
        DeviceSnapshot snapshot = _lastEnumerationFailed
            ? model.Snapshot with { Sequence = _lastSequence, ReadStatus = SnapshotReadStatus.Failed, Resolution = TargetResolution.ReadFailed }
            : model.Snapshot with { Sequence = _lastSequence };
        Volatile.Write(ref _current, snapshot);

        if (!EndpointModelBuilder.AreEquivalent(previous, snapshot) && !IsDisposed)
        {
            _log.Info("Audio devices changed (" + reason + "): " + Describe(model, settings.PinnedContainerId) +
                      (_lastEnumerationFailed ? " The last read failed; these are the last known devices." : ""));
            _uiPost(() => RaiseSnapshotChanged(snapshot));
        }

        return new MonitorRefresh(snapshot, !_lastEnumerationFailed, readings, steps, snapshot.Resolution);
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

        var states = new Dictionary<string, EndpointState>(StringComparer.Ordinal);
        foreach (EndpointReading reading in enumeration.Readings)
        {
            states[reading.Endpoint.EndpointId] = reading.Endpoint.State;
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
                _log.Write(ReadFailureLevel(step, states), "Audio endpoint read failed: " + Describe(step));
            }
        }

        _reportedFailures = seen;
    }

    // Debug only for a failed name read on a NOTPRESENT endpoint, where GetValue(PKEY_Device_FriendlyName)
    // failing with 0xE000020B is expected. Every other failure can change the model, so it is a warning: an
    // unreadable id or flow drops the endpoint, an unreadable state leaves it without one, an unreadable
    // property store or container id moves it out of its device (into the Guid.Empty group, never a target),
    // and a name that cannot be read on a present endpoint can stop the name match.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
    internal static LogLevel ReadFailureLevel(StepOutcome step, IReadOnlyDictionary<string, EndpointState> states)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(states);
        string? endpointId = NameStepEndpoint(step.Step);
        return endpointId is not null && states.TryGetValue(endpointId, out EndpointState state) && state == EndpointState.NotPresent
            ? LogLevel.Debug
            : LogLevel.Warn;
    }

    // The endpoint id of a "friendly-name:<id>" or "interface-name:<id>" step, or null for any other step.
    private static string? NameStepEndpoint(string step)
    {
        foreach (string prefix in NameStepPrefixes)
        {
            if (step.StartsWith(prefix, StringComparison.Ordinal))
            {
                return step[prefix.Length..];
            }
        }

        return null;
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

    private static string Describe(EndpointModel model, Guid pinnedContainerId)
    {
        DeviceSnapshot snapshot = model.Snapshot;
        int endpoints = snapshot.AllGroups.Sum(g => g.Endpoints.Count);
        string groups = snapshot.AllGroups.Count + " groups, " + endpoints + " endpoints";
        if (snapshot.Target is not DeviceModel target)
        {
            return model.Resolution == TargetResolution.PinnedAbsent
                ? "no target device, the pinned container " + pinnedContainerId.ToString("B", CultureInfo.InvariantCulture) + " has no endpoints; " + groups + "."
                : "no target device; " + groups + ".";
        }

        return "target " + target.ContainerId.ToString("B", CultureInfo.InvariantCulture) + " \"" + target.DisplayName + "\" " + target.Connection +
               " (" + (model.Resolution == TargetResolution.Pinned ? "pinned" : "matched by name") + "); " + groups + ".";
    }
}
