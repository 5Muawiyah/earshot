using Earshot.App;
using Earshot.Contracts;

namespace Earshot.Widget;

// Owns the advertisement source's lifetime and turns what it receives into the one snapshot the UI reads.
// Parsing runs on the callback thread the source calls back on; the selection of the owner's AirPods, the
// decoder, the snapshot and the events run after a post through uiPost, so every state change the UI can see
// happens on one thread. Counters are Interlocked so they can always be read without that post.
//
// Which AirPods are the owner's is decided with no step from the owner beyond opening the case next to the PC: the
// paired AirPods' model (read once at start and again when the pinned device changes) picks the candidates, and
// BroadcastSelector links the set whose case is opened near the PC, follows it across address changes and drops the
// link when the set has been lost for too long. The link is in memory only: nothing about it is written, so a restart
// has none. Only the linked set's messages ever reach the values. Its last fresh reading of each part (value, charging
// flag, read time and model, never an address or a name) and the charge rates learned from it are kept in the last
// reading store, read at construction and written on change, and BatteryFreshness shows them (on the card, the gauge and
// its tooltip) wherever the AirPods are, live while fresh and otherwise as a last reading or an estimate. Windows' own
// Hands-Free battery figure, when it has one, is read beside them (every minute while the AirPods are on this PC, at
// each connect, and when asked) and held apart: it fills in only when no bud has a fresh broadcast value.
//
// Start, Suspend, Resume and Close are not on IWidgetStatus: they are called directly by whatever wires the
// widget into the tray (out of scope here), the way the device monitor's own Start is called once and its
// suspend/resume handling sits in the tray's power-event plumbing.
internal sealed class WidgetStatusService : IWidgetStatus, IDisposable
{
    private readonly Func<IAdvertisementSource> _sourceFactory;
    private readonly ISettingsStore _settings;
    private readonly IDeviceMonitor _deviceMonitor;
    private readonly Func<BootBlockStatus?> _blockStatus;
    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _timeProvider;
    private readonly IPairedModelSource _pairedModel;
    private readonly IHandsFreeBatterySource? _handsFree;
    private readonly Action<Action> _runInBackground;

    // Where the owner's pair's last readings and learned rates are kept between runs, or null to keep them in memory only.
    private readonly ILastReadingStore? _lastReadingStore;

    // The decode table is the documented one. Only a test hands in another, to exercise bits the documented table
    // does not set (in-ear, the lid).
    private readonly ProximityDecodeTable _table;

    private readonly Lock _gate = new();
    private readonly Dictionary<(byte? Prefix, int Length), long> _unknownForms = new();
    private readonly BroadcastSelector _selector = new();
    private readonly ChargeRateLearner _learner = new();

    // Rebuilding the shapes list is skipped unless _unknownForms actually changed since the last build: most
    // adverts never touch it at all, so re-allocating it on every one of them would be pointless churn, and
    // (record equality being reference equality for a list) would also make WidgetCounters differ on every
    // publish even when nothing about the unknown-form shapes did.
    private long _unknownFormsVersion;
    private long _cachedUnknownFormsVersion = -1;
    private IReadOnlyList<(byte? Prefix, int Length, long Count)> _cachedUnknownForms = Array.Empty<(byte?, int, long)>();

    private IAdvertisementSource? _source;
    private bool _stopRequested;
    private bool _settingsHooked;
    private bool _started;
    private bool _closed;

    // Bumped once for every Start() call this service makes on the source, whatever it returns; compared
    // against a Stopped event's own Generation to tell a run's genuine end from a late one that already
    // belongs to a run a later Start has superseded. Read and written only under _gate.
    private int _generation;

    // True between Suspend() and Resume(): a retry already queued (or a spontaneous Stopped arriving) while
    // suspended must not start the watcher again until Resume() itself does.
    private bool _suspended;
    private ITimer? _retryTimer;
    private TimeSpan _retryDelay;
    private ITimer? _countersLogTimer;
    private ITimer? _headsetTimer;
    private ITimer? _linkTimer;

    // Due when the linked pair's case counts as closed if no case-known message comes first (CaseOpenTracker.CloseDueAt).
    private ITimer? _caseCloseTimer;
    private WidgetCounters? _lastLoggedCounters;
    private WidgetWatcherState? _lastLoggedWatcherState;

    private long _allSections, _appleSections, _otherCompanySections, _proximityItems;
    private long _okForm, _truncated, _unknownForm;
    private long _modelMismatch, _colourMismatch, _otherSet, _chosen, _noPairedModel;
    private long _budOrderDisagree, _switches, _links, _followed, _drops;
    private int _setsInRange;

    // One line per run, not per message: a form that drifted, and two senders of the chosen set disagreeing on
    // which bud is which. Written and read only on the UI thread, under _gate.
    private bool _driftLogged;
    private bool _budOrderLogged;

    // Everything below is read and written only while holding _gate, so a call from whatever thread the owner's
    // UI runs on and a Received/Stopped callback never race each other.
    private PartReading _left = PartReading.Unknown;
    private PartReading _right = PartReading.Unknown;
    private PartReading _case = PartReading.Unknown;
    private PartReading _headset = PartReading.Unknown;
    private bool? _lastLeftInEar;
    private bool? _lastRightInEar;
    private long _selectionGeneration;
    private DateTimeOffset? _earReadAt;
    private DateTimeOffset? _lastChosenAt;
    private bool _lidOpenBitSeen;
    private bool _lastLidOpenState;

    // Whether the linked pair's case is open, and whether CaseOpened has been raised for an open that has not yet been
    // followed by CaseClosed. Read and written only under _gate.
    private readonly CaseOpenTracker _caseOpen = new();
    private bool _caseOpenRaised;
    private bool _thisPcActive;
    private WidgetWatcherState _watcherState = WidgetWatcherState.NotStarted;
    private int? _watcherErrorCode;
    private string? _watcherErrorName;
    private WidgetSnapshot? _lastPublished;

    // The owner's pair's last readings and learned rates (read from the store at construction), what was last written of
    // them and when, and the latest time anything was worked out at (the estimate's clock, which never goes back).
    private LastReadingBook _book;
    private LastReadingBook _savedBook;
    private DateTimeOffset? _lastSavedAt;
    private DateTimeOffset? _estimateClock;

    // The highest estimate shown for each part's saved reading (read from the store at construction with the readings),
    // the version of it that was last written, and the order books are taken to be written in (LastReadingStore drops one
    // that arrives after a later one). All three are touched holding _gate.
    private readonly EstimateHighWater _highWater;
    private int _savedMarksVersion;
    private long _bookSequence;

    // The pinned device the paired model was last read for, so a settings change that leaves it alone reads nothing.
    private (Guid Container, string Address)? _pairedModelFor;

    // True while a read of the model that was asked for because none is held is running.
    private bool _pairedModelRetryBusy;

    // The container this PC renders to when the AirPods are on it (where Windows' figure would be), and whether a
    // read of that figure is running: one at a time, so a read still running skips the next tick.
    private Guid _watchedContainer;
    private bool _headsetBusy;
    private string? _lastHeadsetNote;

    // The refresh in progress, if any; a second request joins it. Read and written only under _gate.
    private RefreshState? _refresh;

    public WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider,
        IPairedModelSource pairedModel,
        IHandsFreeBatterySource? handsFree = null,
        ILastReadingStore? lastReadings = null)
        : this(sourceFactory, settings, deviceMonitor, blockStatus, log, uiPost, timeProvider, pairedModel, handsFree, ProximityDecodeTable.Documented,
            lastReadings: lastReadings)
    {
    }

    // Test-only: supplies the decode table directly, so a test can exercise bits the documented table does not set, and
    // the way a Hands-Free read is run off the UI thread, so a test can run it where it stands.
    internal WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider,
        IPairedModelSource pairedModel,
        IHandsFreeBatterySource? handsFree,
        ProximityDecodeTable table,
        Action<Action>? runInBackground = null,
        ILastReadingStore? lastReadings = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceMonitor);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pairedModel);
        ArgumentNullException.ThrowIfNull(table);

        _sourceFactory = sourceFactory;
        _settings = settings;
        _deviceMonitor = deviceMonitor;
        _blockStatus = blockStatus;
        _log = log;
        _uiPost = uiPost;
        _timeProvider = timeProvider;
        _pairedModel = pairedModel;
        _handsFree = handsFree;
        _runInBackground = runInBackground ?? (static work => _ = Task.Run(work));
        _table = table;

        // The last readings are read once, at start, and shown from then on until newer ones are heard.
        _lastReadingStore = lastReadings;
        _book = lastReadings?.Load() ?? LastReadingBook.Empty;
        _savedBook = _book;
        _highWater = new EstimateHighWater(_book.Marks);
        _savedMarksVersion = _highWater.Version;
    }

    public event EventHandler? Changed;

    // Raised when the linked pair's case opens (CaseOpenTracker: its case-known messages starting after silence, a link
    // made by a case open, or its lid open counter changing while it stays open), and once more for every further open.
    public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

    // Raised once after a CaseOpened when the linked pair's case-known messages have stopped for CaseOpenTracker.CloseAfter,
    // or the link that was open was dropped or replaced by nothing.
    public event EventHandler<CaseClosedEventArgs>? CaseClosed;

    // Raised for every reading of the linked set, and of no other: the seam auto-pause is fed from. Auto-pause itself acts
    // only while the AirPods are this PC's output.
    public event EventHandler<ReadingAppliedEventArgs>? ReadingApplied;

    public WidgetSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotLocked(_timeProvider.GetUtcNow());
            }
        }
    }

    // Constructed and started only while the setting is on; hooks the device monitor and settings change
    // regardless, so turning the setting on later (TurningTheSettingOnStartsOne) still works. Idempotent: a
    // second call does nothing, rather than double-subscribing the device monitor and settings events and
    // constructing a second advertisement source over the first, still-running one.
    public void Start()
    {
        IAdvertisementSource? sourceToStart = null;
        int generation = 0;
        bool startOnThisPc;
        lock (_gate)
        {
            if (_started || _closed)
            {
                return;
            }

            _started = true;
            RecomputeThisPcLocked(_deviceMonitor.Current);
            startOnThisPc = _thisPcActive;
            _deviceMonitor.SnapshotChanged += OnDeviceSnapshotChanged;

            if (!_settingsHooked)
            {
                _settings.Changed += OnSettingsChanged;
                _settingsHooked = true;
            }

            if (_settings.Current.Widget.Enabled)
            {
                sourceToStart = CreateSourceLocked();
                _generation++;
                generation = _generation;
            }
            else
            {
                _watcherState = WidgetWatcherState.Off;
            }

            // The counters line: once a minute while anything changed, never on a fixed schedule regardless.
            _countersLogTimer ??= _timeProvider.CreateTimer(
                static state => ((WidgetStatusService)state!).OnCountersLogDue(), this,
                WidgetTiming.CountersLogInterval, WidgetTiming.CountersLogInterval);

            // Silence brings no message to notice it with, so a linked set that has been lost for too long is found by
            // looking at the clock now and then.
            _linkTimer ??= _timeProvider.CreateTimer(
                static state => ((WidgetStatusService)state!).OnLinkCheckDue(), this,
                WidgetTiming.LinkCheckInterval, WidgetTiming.LinkCheckInterval);

            // Windows' own figure is read once a minute, only while the AirPods are on this PC (the tick checks).
            if (_handsFree is not null)
            {
                _headsetTimer ??= _timeProvider.CreateTimer(
                    static state => ((WidgetStatusService)state!).OnHeadsetPollDue(), this,
                    WidgetTiming.HeadsetPollInterval, WidgetTiming.HeadsetPollInterval);
            }
        }

        if (sourceToStart is not null)
        {
            // The model is read before the source starts, so a message heard at once already has a paired model to
            // be compared with.
            ReadPairedModel(force: true, publish: false);

            // Unlike every other call site of RunStartOutsideLock, the very first Start() never publishes:
            // nothing has anything to compare its first snapshot against yet, and it matches what the
            // original, single-lock version of this method did (a caller watching uiPost's own queue,
            // ChangedAndCaseOpenedAreRaisedThroughUiPost, pins it).
            RunStartOutsideLock(sourceToStart, generation, publish: false);

            // Already on this PC when the widget starts: Windows' figure is read once now.
            if (startOnThisPc)
            {
                RequestHeadsetRead();
            }
        }
    }

    public void Suspend()
    {
        IAdvertisementSource? sourceToStop;
        lock (_gate)
        {
            if (_source is null || _closed)
            {
                return;
            }

            _suspended = true;
            sourceToStop = BeginStopLocked();
        }

        if (sourceToStop is not null)
        {
            RunStopOutsideLock(sourceToStop, disposeSource: false);
        }

        CompleteAnyRefresh(BatteryRefreshOutcome.NotListening);
        PublishAndNotify();
    }

    public void Resume()
    {
        IAdvertisementSource? sourceToStart;
        int generation;
        lock (_gate)
        {
            if (_source is null || !_settings.Current.Widget.Enabled || _closed)
            {
                return;
            }

            _suspended = false;
            _stopRequested = false;
            _generation++;
            generation = _generation;
            sourceToStart = _source;
        }

        RunStartOutsideLock(sourceToStart, generation);
    }

    // Idempotent, and final: once closed, nothing on this service raises Changed or CaseOpened again, whatever
    // calls it (or an in-flight callback that was already dispatched before this ran) tries next.
    public void Close()
    {
        IAdvertisementSource? sourceToStop;
        LastReadingBook? bookToSave = null;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            if (_settingsHooked)
            {
                _settings.Changed -= OnSettingsChanged;
                _settingsHooked = false;
            }

            _countersLogTimer?.Dispose();
            _countersLogTimer = null;
            _headsetTimer?.Dispose();
            _headsetTimer = null;
            _linkTimer?.Dispose();
            _linkTimer = null;
            _caseCloseTimer?.Dispose();
            _caseCloseTimer = null;

            _deviceMonitor.SnapshotChanged -= OnDeviceSnapshotChanged;
            sourceToStop = BeginStopLocked();

            // A read time not yet written is written now, so the next start shows the reading as old as it is, and so is an
            // estimate shown since the last write, so a restart shows no less.
            if (_lastReadingStore is not null && (!ReferenceEquals(_book, _savedBook) || _highWater.Version != _savedMarksVersion))
            {
                bookToSave = TakeForSaveLocked(_book);
                _savedBook = _book;
            }
        }

        if (bookToSave is not null)
        {
            _lastReadingStore!.Save(bookToSave);
        }

        if (sourceToStop is not null)
        {
            RunStopOutsideLock(sourceToStop, disposeSource: true);
        }

        CompleteAnyRefresh(BatteryRefreshOutcome.NotListening);
    }

    public void Dispose() => Close();

    // One immediate retry when the watcher is not running; the doubling timer keeps trying regardless.
    public Task RefreshAsync()
    {
        bool tryStart;
        lock (_gate)
        {
            tryStart = !_closed && _source is not null && _source.State != AdvertisementSourceState.Started;
        }

        if (tryStart)
        {
            RetryStartNow();
        }

        return Task.CompletedTask;
    }

    // Constructs a fresh source and subscribes its events, cheap and under the lock; the source's own
    // Start() (which can block, and for the real watcher is a WinRT call) happens afterwards, outside it -
    // see RunStartOutsideLock. The generation this Start() call will make is the caller's to bump and pass
    // through: a fresh instance never numbers its own runs, so it never has to coordinate that count with
    // whatever instance it replaces.
    private IAdvertisementSource CreateSourceLocked()
    {
        var source = _sourceFactory();
        source.Received += OnReceived;
        source.Stopped += OnStopped;
        _source = source;
        _stopRequested = false;
        return source;
    }

    // Start and Stop must run outside the service's own lock, since a handler that needs the same lock
    // (Received, Stopped, or a public call such as Current) would otherwise be able to deadlock against one
    // of them - a fake whose Stop blocks until such a handler has taken the lock proves it either way. The
    // lock is only reacquired here to apply the result, and only if the source this call started is still
    // the one the service knows about (nothing else replaced or closed it meanwhile). publish is false only
    // for the very first Start(): see its own call site's comment.
    private void RunStartOutsideLock(IAdvertisementSource source, int generation, bool publish = true)
    {
        StepOutcome step = source.Start(generation);
        bool stopWhatJustStarted = false;
        bool disposeWhatJustStarted = false;
        lock (_gate)
        {
            bool stillTracked = !_closed && ReferenceEquals(_source, source);
            if (!stillTracked)
            {
                // Close, or the setting going off, can each run between source.Start() returning and this
                // lock being retaken (the call itself is a blocking WinRT call outside the lock, see above).
                // Neither's own stop pass is guaranteed to still catch this exact source once it has moved
                // _source on or nulled it: whatever the race just (re)started here must be stopped and
                // disposed on its own, or nothing left tracking it ever will.
                if (step.Ok)
                {
                    stopWhatJustStarted = true;
                    disposeWhatJustStarted = true;
                }
            }
            else
            {
                ApplyStartStepLocked(step);
                if (_watcherState == WidgetWatcherState.Started)
                {
                    // Suspend can also run between source.Start() returning and this lock being retaken;
                    // re-check it here rather than trusting the snapshot this call started with, or a source
                    // that raced past that check is left running with the service believing it is idle.
                    if (_suspended || _stopRequested)
                    {
                        stopWhatJustStarted = true;
                        _watcherState = WidgetWatcherState.Stopped;
                    }
                    else
                    {
                        _retryDelay = TimeSpan.Zero;
                        CancelRetryLocked();
                    }
                }
                else
                {
                    // A Start that throws or fails synchronously, with no Stopped event ever coming to
                    // trigger OnStopped's own retry, must still get one scheduled here - whatever called this
                    // (the very first start, a settings toggle, Resume, or a manual refresh).
                    ScheduleRetryLocked();
                }
            }
        }

        if (stopWhatJustStarted)
        {
            RunStopOutsideLock(source, disposeSource: disposeWhatJustStarted);
        }

        if (publish)
        {
            PublishAndNotify();
        }
    }

    private void ApplyStartStepLocked(StepOutcome step)
    {
        // A successful Start() request can leave the real
        // watcher's own Status reading Created for a moment before it settles to Started (documented on
        // WinRtAdvertisementSourceBindingTests' own wait helper), and reading it again immediately here used
        // to show that as Stopped, with the successful step's own code shown as if it were the error. Aborted
        // is the one state Start() can return synchronously as a genuine failure; anything else the state
        // reads after an Ok step is a run still settling, not a failed one.
        AdvertisementSourceState state = _source!.State;
        bool started = step.Ok && state != AdvertisementSourceState.Aborted;
        _watcherState = started ? WidgetWatcherState.Started : WidgetWatcherState.Stopped;
        if (started)
        {
            _watcherErrorCode = null;
            _watcherErrorName = null;
        }
        else
        {
            // A failed start's own error must stand, not be discarded the way it used to be here - a
            // Stopped event is not coming to carry it, since none was ever raised for this attempt.
            if (AdvertisementSourceCodes.IsRadioOff(step))
            {
                // Bluetooth is off: shown as the RadioNotAvailable a Stopped event would have carried, so
                // the card and the retry treat both the same way. The raw code stays in the log
                // line below and in the step.
                _watcherErrorCode = AdvertisementSourceCodes.RadioNotAvailableCode;
                _watcherErrorName = AdvertisementSourceCodes.RadioNotAvailableName;
            }
            else
            {
                _watcherErrorCode = step.Code;
                _watcherErrorName = step.CodeName;
            }
        }

        LogStepLocked("start", step);
    }

    // Every Stop step is logged with its code and detail too, not just Start's; at Warn when not ok, so
    // a stop that failed (rather than simply never having been started) is visible in the log.
    private void ApplyStopStepLocked(StepOutcome step) => LogStepLocked("stop", step);

    private void LogStepLocked(string verb, StepOutcome step)
    {
        string message = "Widget watcher " + verb + ": " + step.Step + " " + step.CodeName +
            (step.Detail is string detail ? " (" + detail + ")" : string.Empty) + ".";
        if (step.Ok)
        {
            _log.Info(message);
        }
        else
        {
            _log.Warn(message);
        }
    }

    // Marks the source as being stopped and cancels any pending retry, under the lock; the source's own
    // Stop() call happens afterwards, outside it, matching every other call site's own outside-the-lock
    // rule. Returns the source to stop, or null when there is none.
    private IAdvertisementSource? BeginStopLocked()
    {
        if (_source is null)
        {
            return null;
        }

        _stopRequested = true;
        CancelRetryLocked();
        return _source;
    }

    // See RunStartOutsideLock's comment: the same reasoning applies to Stop. disposeSource is false for
    // Suspend (the same source instance is reused on Resume) and true for Close and turning the setting off.
    private void RunStopOutsideLock(IAdvertisementSource source, bool disposeSource)
    {
        StepOutcome step = source.Stop();
        lock (_gate)
        {
            if (!ReferenceEquals(_source, source))
            {
                return; // already replaced or disposed by something else meanwhile
            }

            ApplyStopStepLocked(step);
            if (disposeSource)
            {
                source.Received -= OnReceived;
                source.Stopped -= OnStopped;
                source.Dispose();
                _source = null;
                _watcherState = WidgetWatcherState.Off;
            }
            else
            {
                _watcherState = WidgetWatcherState.Stopped;
            }
        }
    }

    private void OnSettingsChanged(object? sender, EarshotSettings settings)
    {
        IAdvertisementSource? sourceToStart = null;
        IAdvertisementSource? sourceToStop = null;
        int generation = 0;
        bool changed;
        lock (_gate)
        {
            bool enabled = settings.Widget.Enabled;
            if (enabled && _source is null)
            {
                sourceToStart = CreateSourceLocked();
                _generation++;
                generation = _generation;
                changed = true;
            }
            else if (!enabled && _source is not null)
            {
                sourceToStop = BeginStopLocked();
                changed = true;
            }
            else
            {
                changed = false;
            }
        }

        if (sourceToStart is not null)
        {
            ReadPairedModel(force: true, publish: false);
            RunStartOutsideLock(sourceToStart, generation); // publishes itself
            return;
        }

        if (sourceToStop is not null)
        {
            RunStopOutsideLock(sourceToStop, disposeSource: true);
        }

        // A different pinned device is a different pair of AirPods: its model is read again, with the widget on.
        if (settings.Widget.Enabled)
        {
            ReadPairedModel(force: false, publish: true);
        }

        if (changed)
        {
            PublishAndNotify();
        }
    }

    private void OnDeviceSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        bool justConnected;
        lock (_gate)
        {
            bool wasThisPc = _thisPcActive;
            RecomputeThisPcLocked(e.Snapshot);
            justConnected = _thisPcActive && !wasThisPc;
        }

        // Windows' figure is read once at each connect.
        if (justConnected)
        {
            RequestHeadsetRead();
        }

        PublishAndNotify();
    }

    private void RecomputeThisPcLocked(DeviceSnapshot snapshot)
    {
        Guid watched = CoordinatorRules.WatchedContainer(_blockStatus(), _settings.Current, snapshot);
        _watchedContainer = watched;
        _thisPcActive = CoordinatorRules.RenderOf(snapshot, watched) == RenderState.Active;
    }

    // ---- Battery refresh

    // What a refresh in progress is waiting for. WindowsFigure is set when a read of Windows' own figure found one
    // while it waited.
    private sealed class RefreshState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public TaskCompletionSource<BatteryRefreshOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Timer { get; set; }

        public bool WindowsFigure { get; set; }
    }

    // Restarts the passive listen through the same outside-the-lock stop and start every other restart uses (a new
    // generation, so the old run's own Stopped never reads as the current one), starts a read of Windows' own figure,
    // and waits for a message of the chosen set sent after the restart, or for WidgetTiming.RefreshWindow to pass.
    // A passive watcher sends no scan request and connects to nothing, so restarting it pages nothing, and the figure
    // read is a property read: the AirPods stay unpaged at rest. No value is cleared. A second request while one runs
    // joins it; cancelling ends only the caller's own wait.
    public Task<BatteryRefreshOutcome> RefreshBatteryAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<BatteryRefreshOutcome>(ct);
        }

        RefreshState state;
        lock (_gate)
        {
            if (_refresh is { } running)
            {
                return running.Completion.Task.WaitAsync(ct);
            }

            if (_closed || _source is null || _suspended)
            {
                return Task.FromResult(BatteryRefreshOutcome.NotListening);
            }

            state = new RefreshState(_timeProvider.GetUtcNow());
            _refresh = state;
        }

        RereadPairedModelIfNone();
        StartRefresh(state);
        return state.Completion.Task.WaitAsync(ct);
    }

    private void StartRefresh(RefreshState state)
    {
        bool running;
        lock (_gate)
        {
            running = _watcherState == WidgetWatcherState.Started;
        }

        if (running)
        {
            RestartWatcherForRefresh();
        }
        else
        {
            // Stopped (Bluetooth was off, or a start failed): one immediate attempt, the way RefreshAsync makes one.
            RetryStartNow();
        }

        BatteryRefreshOutcome? failed;
        lock (_gate)
        {
            failed = _watcherState == WidgetWatcherState.Started
                ? null
                : _watcherErrorCode == AdvertisementSourceCodes.RadioNotAvailableCode ? BatteryRefreshOutcome.BluetoothOff : BatteryRefreshOutcome.NotListening;
        }

        if (failed is { } outcome)
        {
            CompleteRefresh(state, outcome);
            return;
        }

        ITimer timer = _timeProvider.CreateTimer(
            static box => ((RefreshTimerBox)box!).Service.OnRefreshWindowElapsed(((RefreshTimerBox)box).State), new RefreshTimerBox(this, state),
            WidgetTiming.RefreshWindow, Timeout.InfiniteTimeSpan);
        bool done;
        lock (_gate)
        {
            done = state.Completion.Task.IsCompleted;
            if (!done)
            {
                state.Timer = timer;
            }
        }

        if (done)
        {
            timer.Dispose();
            return;
        }

        RequestHeadsetRead();
    }

    private sealed record RefreshTimerBox(WidgetStatusService Service, RefreshState State);

    private void OnRefreshWindowElapsed(RefreshState state)
    {
        bool windows;
        lock (_gate)
        {
            windows = state.WindowsFigure;
        }

        CompleteRefresh(state, windows ? BatteryRefreshOutcome.WindowsFigure : BatteryRefreshOutcome.NothingHeard);
    }

    // The stop and start of Suspend and Resume without the suspended flag between them, so nothing else is held off.
    private void RestartWatcherForRefresh()
    {
        IAdvertisementSource? stop;
        lock (_gate)
        {
            if (_source is null || _closed || _suspended)
            {
                return;
            }

            stop = BeginStopLocked();
        }

        if (stop is not null)
        {
            RunStopOutsideLock(stop, disposeSource: false);
        }

        IAdvertisementSource? start;
        int generation;
        lock (_gate)
        {
            if (_closed || _suspended || !ReferenceEquals(_source, stop))
            {
                return;
            }

            _stopRequested = false;
            _generation++;
            generation = _generation;
            start = _source;
        }

        RunStartOutsideLock(start, generation);
    }

    // Ends the refresh in progress with this outcome, once. The completion runs its continuations off this thread.
    private void CompleteRefresh(RefreshState state, BatteryRefreshOutcome outcome)
    {
        ITimer? timer;
        lock (_gate)
        {
            if (!ReferenceEquals(_refresh, state))
            {
                return;
            }

            _refresh = null;
            timer = state.Timer;
            state.Timer = null;
        }

        timer?.Dispose();
        _log.Info("Widget: battery refresh ended: " + outcome + ".");
        state.Completion.TrySetResult(outcome);
    }

    private void CompleteAnyRefresh(BatteryRefreshOutcome outcome)
    {
        RefreshState? state;
        lock (_gate)
        {
            state = _refresh;
        }

        if (state is not null)
        {
            CompleteRefresh(state, outcome);
        }
    }

    // ---- Windows' Hands-Free figure

    private void OnHeadsetPollDue()
    {
        lock (_gate)
        {
            if (!_thisPcActive)
            {
                return; // Windows' figure is never shown for AirPods that are not on this PC, so it is not read for them
            }
        }

        RequestHeadsetRead();
    }

    // Starts one read of Windows' figure off the UI thread, unless one is running, the widget is off or suspended, or
    // there is no source. The result comes back through uiPost.
    internal void RequestHeadsetRead()
    {
        Guid container;
        string address;
        lock (_gate)
        {
            if (_handsFree is null || _closed || _suspended || _source is null || _headsetBusy)
            {
                return;
            }

            _headsetBusy = true;
            container = _watchedContainer != Guid.Empty ? _watchedContainer : _settings.Current.PinnedContainerId;
            address = _settings.Current.PinnedAddress;
        }

        _runInBackground(() => ReadHeadsetOffTheUiThread(container, address));
    }

    // The source never throws for anything Windows did; the one catch is a bug in this process on a worker thread, which
    // would otherwise end it. The raw code is logged, never swallowed.
    private void ReadHeadsetOffTheUiThread(Guid container, string address)
    {
        HandsFreeBatteryRead? read = null;
        try
        {
            read = _handsFree!.Read(container, address);
        }
        catch (Exception ex)
        {
            _log.Error("Widget: reading Windows' Hands-Free battery failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }

        DateTimeOffset at = _timeProvider.GetUtcNow();
        _uiPost(() => ApplyHeadsetRead(read, at));
    }

    private void ApplyHeadsetRead(HandsFreeBatteryRead? read, DateTimeOffset at)
    {
        lock (_gate)
        {
            _headsetBusy = false;
            if (_closed || read is null)
            {
                return;
            }

            if (read.Percent is int percent && _thisPcActive)
            {
                _headset = new PartReading(percent, null, null) { ReadAt = at };
                if (_refresh is { } waiting)
                {
                    waiting.WindowsFigure = true;
                }
            }
            else
            {
                // A read that found no figure says there is none now, so the figure an earlier read found is
                // dropped rather than left to age. One that finished after the AirPods left this PC is not
                // theirs to show here either: Windows' figure only ever stands for AirPods on this PC.
                _headset = PartReading.Unknown;
            }
        }

        // What was found is logged when it changes, not every minute: the raw code of a failing step, and the one line
        // that says why there was no figure.
        string text = string.Join(
            "; ", read.Steps.Where(step => !step.Ok).Select(step => step.Step + " " + step.CodeName)
                .Append(read.Percent is int found ? "a figure from a " + read.Origin : read.Note ?? "no figure"));
        bool changed;
        lock (_gate)
        {
            changed = _lastHeadsetNote != text;
            _lastHeadsetNote = text;
        }

        if (changed)
        {
            bool failed = read.Steps.Any(step => !step.Ok);
            string line = "Widget: Windows' Hands-Free battery: " + text + ".";
            if (failed)
            {
                _log.Warn(line);
            }
            else
            {
                _log.Info(line);
            }
        }

        PublishAndNotify();
    }

    // Reads the paired AirPods' model for the pinned device and hands it to the selector. The read is CfgMgr32 work
    // and runs outside the lock. A read that finds no model says so in the log with the raw code of every step that
    // failed, and while there is none the card shows no reading. A model that differs from the last one starts the
    // selection over, so whatever values were kept (they belonged to another pair) are dropped. publish is false
    // where the caller publishes itself, or where nothing has anything to compare a snapshot against yet.
    private void ReadPairedModel(bool force, bool publish)
    {
        EarshotSettings current = _settings.Current;
        Guid container = current.PinnedContainerId;
        string address = current.PinnedAddress;
        lock (_gate)
        {
            if (_closed || (!force && _pairedModelFor is { } last && last.Container == container && last.Address == address))
            {
                return;
            }

            _pairedModelFor = (container, address);
        }

        PairedModelRead read = _pairedModel.Read(container, address);
        bool reset;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            reset = _selector.SetPairedModel(read.Model);
            if (reset)
            {
                ResetReadingsLocked();
            }
        }

        foreach (StepOutcome step in read.Steps.Where(s => !s.Ok))
        {
            _log.Warn("Widget paired model: " + step.Step + " " + step.CodeName + (step.Detail is string detail ? " (" + detail + ")" : string.Empty) + ".");
        }

        if (read.Model is ushort model)
        {
            _log.Info("Widget: the paired AirPods' model is 0x" + model.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }
        else
        {
            _log.Warn("Widget: no paired AirPods model could be read, so no battery is shown.");
        }

        if (reset && publish)
        {
            PublishAndNotify();
        }
    }

    // A model that could not be read (the device was not listed yet, or the list failed) is read again whenever the
    // listening is retried or a refresh is asked for, while none is held: otherwise a failed read at start would
    // leave the card with no reading until the pinned device changed. The read runs off the calling thread, one at a
    // time, and its result is published like any other change of the model.
    private void RereadPairedModelIfNone()
    {
        lock (_gate)
        {
            if (_closed || _pairedModelRetryBusy || _selector.PairedModel is not null)
            {
                return;
            }

            _pairedModelRetryBusy = true;
        }

        _runInBackground(ReadPairedModelAgain);
    }

    // On a worker thread, where an exception nothing handles would end the process: the one catch is a bug in this
    // process, logged with its code, never swallowed.
    private void ReadPairedModelAgain()
    {
        try
        {
            ReadPairedModel(force: true, publish: true);
        }
        catch (Exception ex)
        {
            _log.Error("Widget: reading the paired AirPods' model again failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }
        finally
        {
            lock (_gate)
            {
                _pairedModelRetryBusy = false;
            }
        }
    }

    // Runs on whatever thread the source calls back on: the parse happens here, and every state change the
    // UI can observe is then posted through uiPost onto the UI thread. Every Apple 0x07 section, however it
    // parsed, is posted on; anything else (a different company, or Apple data with no 0x07 item at all) ends
    // here.
    private void OnReceived(object? sender, AdvertisementSample sample)
    {
        Interlocked.Increment(ref _allSections);
        if (sample.CompanyId != ProximityParser.AppleCompanyId)
        {
            Interlocked.Increment(ref _otherCompanySections);
            return;
        }

        Interlocked.Increment(ref _appleSections);
        ProximityParse parse = ProximityParser.Parse(sample.CompanyId, sample.Data);
        Interlocked.Add(ref _proximityItems, parse.ProximityItems);

        switch (parse.Status)
        {
            case ProximityParseStatus.Ok:
                Interlocked.Increment(ref _okForm);
                break;
            case ProximityParseStatus.Truncated:
                Interlocked.Increment(ref _truncated);
                break;
            case ProximityParseStatus.UnknownForm:
                Interlocked.Increment(ref _unknownForm);
                RecordUnknownForm(parse.Prefix, parse.Length ?? 0);
                break;
            default:
                // WrongType: an Apple section with no 0x07 item. The section-level counters above already
                // account for it; nothing else happens on this thread.
                return;
        }

        DateTimeOffset at = sample.Timestamp;
        uint tag = sample.SenderTag;
        sbyte rssi = sample.Rssi;
        _uiPost(() => HandleParsedOnUiThread(parse, tag, rssi, at));
    }

    private void RecordUnknownForm(byte? prefix, int length)
    {
        lock (_gate)
        {
            var key = (prefix, length);
            if (_unknownForms.TryGetValue(key, out long count))
            {
                _unknownForms[key] = count + 1;
                _unknownFormsVersion++;
            }
            else if (_unknownForms.Count < WidgetCounters.MaxUnknownFormShapes)
            {
                _unknownForms[key] = 1;
                _unknownFormsVersion++;
            }
        }
    }

    private void HandleParsedOnUiThread(ProximityParse parse, uint tag, sbyte rssi, DateTimeOffset at)
    {
        // Only the documented 25-byte form is ever decoded or compared: the 17-byte form and every other shape stay
        // unknown forms, counted by shape.
        if (parse.Status == ProximityParseStatus.Ok && parse.Message is ProximityMessage message)
        {
            ApplyMessage(message, tag, rssi, at);
        }

        PublishAndNotify();
    }

    private void ApplyMessage(ProximityMessage message, uint tag, sbyte rssi, DateTimeOffset at)
    {
        bool caseOpenedEdge = false;
        DateTimeOffset caseOpenedAt = at;
        DecodedReading? applied = null;
        long generation = 0;
        RefreshState? heard = null;
        LastReadingBook? toSave = null;

        lock (_gate)
        {
            if (_closed)
            {
                return; // nothing after Close raises an event
            }

            // 11 to 14 are a shape neither permitted source describes at all (unlike the documented 0xF
            // "unknown"), so it is logged as the form drifting rather than treated the same as ordinary unknown.
            // Never the nibble value itself: only that one was out of range, and once a run rather than once a
            // message.
            if (!_driftLogged &&
                (BatteryNibble.IsOutOfRange((message.BatteryA >> 4) & 0x0F) ||
                 BatteryNibble.IsOutOfRange(message.BatteryA & 0x0F) ||
                 BatteryNibble.IsOutOfRange(message.BatteryB & 0x0F)))
            {
                _driftLogged = true;
                _log.Warn("Widget: a battery nibble read 11 to 14, a shape the documented form does not describe: the form may have drifted.");
            }

            SelectionObservation seen = _selector.Observe(message, tag, rssi, at);
            if (seen.SetsInRange > 0)
            {
                _setsInRange = seen.SetsInRange;
            }

            if (seen.Dropped)
            {
                // The linked set was lost for longer than the limit before this message: nothing is linked, so nothing of
                // what it said is kept.
                DropLinkLocked();
            }

            if (seen.NewChoice)
            {
                // A set was linked that is not the one whose values are held (a case opened near the PC, or another pair
                // that opened its case clearly nearer): those values were another pair's, so they are dropped.
                ResetReadingsLocked();
                if (seen.Switched)
                {
                    _switches++;
                    _log.Info("Widget: another set of AirPods opened its case clearly nearer, so the link moved to it (" + seen.SetsInRange + " in range).");
                }
                else
                {
                    _links++;
                    _log.Info("Widget: linked to the AirPods whose case was opened (" + seen.SetsInRange + " in range).");
                }
            }
            else if (seen.Followed)
            {
                // The values on show stay, greyed as they age, until this set's own messages replace them. The in-ear
                // state does not: whoever pairs readings by set is told the set changed addresses.
                _selectionGeneration++;
                _followed++;
                _log.Info("Widget: the linked set changed address and was followed (" + seen.SetsInRange + " in range).");
            }

            switch (seen.Class)
            {
                case BroadcastClass.NoPairedModel:
                    Interlocked.Increment(ref _noPairedModel);
                    return;
                case BroadcastClass.ModelMismatch:
                    Interlocked.Increment(ref _modelMismatch);
                    return;
                case BroadcastClass.ColourMismatch:
                    Interlocked.Increment(ref _colourMismatch);
                    return;
                case BroadcastClass.NotLinked:
                    return;
                case BroadcastClass.OtherSet:
                    Interlocked.Increment(ref _otherSet);
                    return;
                default:
                    Interlocked.Increment(ref _chosen);
                    break;
            }

            DecodedReading reading = ProximityDecoder.Decode(message, _table, at);
            CheckBudOrderLocked(reading, tag, at);
            ApplyDecodedReadingLocked(reading, _table, at);
            caseOpenedEdge = ObserveCaseLocked(tag, message, reading, at);
            toSave = RecordLastReadingLocked(reading, message.Model, at);
            applied = reading;
            generation = _selectionGeneration;

            // A message of the chosen set sent after the refresh restarted the listen is the answer it waits for.
            if (_refresh is { } waiting && at >= waiting.StartedAt)
            {
                heard = waiting;
            }
        }

        if (toSave is not null)
        {
            _lastReadingStore?.Save(toSave);
        }

        if (heard is not null)
        {
            CompleteRefresh(heard, BatteryRefreshOutcome.Heard);
        }

        if (caseOpenedEdge)
        {
            _uiPost(() => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(caseOpenedAt)));
        }

        if (applied is DecodedReading decoded)
        {
            _uiPost(() => ReadingApplied?.Invoke(this, new ReadingAppliedEventArgs(decoded, at, generation)));
        }
    }

    // The two buds of the chosen set each send on their own address and each carry the pair swapped. Both decode to
    // the same left and right under the decode table's rule; if two of them, heard within two seconds, do not, the
    // rule is wrong about which side is which for some message. Counted, and logged once per run.
    private void CheckBudOrderLocked(DecodedReading reading, uint tag, DateTimeOffset at)
    {
        foreach (SenderMessage other in _selector.OtherChosenSenders(tag, at, BroadcastRules.SameSetWithin))
        {
            DecodedReading counterpart = ProximityDecoder.Decode(other.Message, _table, other.At);
            if (Differs(reading.Left.Percent, counterpart.Left.Percent) || Differs(reading.Right.Percent, counterpart.Right.Percent))
            {
                Interlocked.Increment(ref _budOrderDisagree);
                if (!_budOrderLogged)
                {
                    _budOrderLogged = true;
                    _log.Warn("Widget: two senders of the chosen set, heard within two seconds, read different left and right levels.");
                }
            }
        }
    }

    private static bool Differs(int? a, int? b) => a is int x && b is int y && x != y;

    // The link was dropped because the linked set was lost for longer than BroadcastRules.LostLimit: nothing it said is
    // kept, and the drop is counted and logged as numbers only, never an address. Must be called holding _gate.
    private void DropLinkLocked()
    {
        ResetReadingsLocked();
        _drops++;
        _setsInRange = 0;
        _log.Info("Widget: the linked AirPods were not heard for " + (long)BroadcastRules.LostLimit.TotalSeconds + " s, so the link was dropped.");
    }

    // Runs on a real timer thread, so like the other timers it catches what nothing else would, logs it with its code and
    // never lets it end the process.
    private void OnLinkCheckDue()
    {
        try
        {
            bool dropped;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                dropped = _selector.Expire(_timeProvider.GetUtcNow());
                if (dropped)
                {
                    DropLinkLocked();
                }
            }

            if (dropped)
            {
                PublishAndNotify();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Widget link check failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }
    }

    // A fresh reading of the linked set, and of no other, goes into the owner's last readings, and the parts' charge runs
    // learn from it. Returns the book to write when it is due (a value, a flag, a model or a rate changed at once; a read
    // time alone at most every LastReadingStore.ReadTimeWriteInterval), or null. Must be called holding _gate; the write
    // itself happens outside it.
    private LastReadingBook? RecordLastReadingLocked(DecodedReading reading, ushort model, DateTimeOffset at)
    {
        LastReadingBook book = _book.WithReading(reading, model, at);
        foreach ((ChargeComponent component, PartReading part) in new[]
        {
            (ChargeComponent.Left, reading.Left), (ChargeComponent.Right, reading.Right), (ChargeComponent.Case, reading.Case),
        })
        {
            if (part.Percent is int percent && _learner.Observe(component, model, percent, part.Charging == true, at) is { } rate)
            {
                book = book.WithRate(rate);
                _log.Info("Widget: a charge rate was learned for the " + (rate.Part == ChargePart.Case ? "case" : "buds") + ": " +
                    Math.Round(rate.PercentPerHour).ToString(System.Globalization.CultureInfo.InvariantCulture) + " points an hour over " +
                    Math.Round(rate.Span.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) + " min.");
            }
        }

        _book = book;
        if (_lastReadingStore is null || ReferenceEquals(book, _savedBook))
        {
            return null;
        }

        bool due = LastReadingBook.DiffersBeyondReadTimes(book, _savedBook) ||
            _lastSavedAt is not DateTimeOffset last || (at - last).Duration() >= LastReadingStore.ReadTimeWriteInterval;
        if (!due)
        {
            return null;
        }

        _savedBook = book;
        _lastSavedAt = at;
        return TakeForSaveLocked(book);
    }

    // The book as it is written: the estimates shown for its readings, and the next number in the order of writes. Must be
    // called holding _gate, so the order of the numbers is the order the books were taken in whichever thread writes first.
    private LastReadingBook TakeForSaveLocked(LastReadingBook book)
    {
        _savedMarksVersion = _highWater.Version;
        return book with { Marks = _highWater.MarksFor(book), Sequence = ++_bookSequence };
    }

    // Whatever the linked set held is dropped: after a link that is another set, or a new paired model, nothing
    // of the old values may be shown against the new pair. The saved last readings stay: they are the owner's pair's, and
    // a newer fresh reading of the linked set replaces them part by part. The charge runs start over.
    private void ResetReadingsLocked()
    {
        _selectionGeneration++;
        _learner.Reset();
        _left = PartReading.Unknown;
        _right = PartReading.Unknown;
        _case = PartReading.Unknown;
        _lastLeftInEar = null;
        _lastRightInEar = null;
        _earReadAt = null;
        _lastChosenAt = null;
        _lidOpenBitSeen = false;
        _lastLidOpenState = false;

        // A case that was open was another pair's, or a pair no longer linked: PublishAndNotify raises CaseClosed for it
        // unless the message that made the new link opens it again.
        _caseOpen.Reset();
    }

    // Must be called holding _gate. Feeds the case-open tracker one message of the linked set, keeps the close timer due
    // at the tracker's close time, and returns true when this message is an open, for CaseOpened.
    private bool ObserveCaseLocked(uint tag, ProximityMessage message, DecodedReading reading, DateTimeOffset at)
    {
        CaseOpenStep step = _caseOpen.Observe(tag, BroadcastSenderSets.CaseKnown(message), reading.LidCounter, at);
        if (step == CaseOpenStep.Opened)
        {
            _caseOpenRaised = true;
        }

        ScheduleCaseCloseLocked();
        return step == CaseOpenStep.Opened;
    }

    // Must be called holding _gate. Silence brings no message, so a timer due at the tracker's close time notices it.
    private void ScheduleCaseCloseLocked()
    {
        if (_closed || _caseOpen.CloseDueAt is not { } due)
        {
            return;
        }

        TimeSpan wait = due - _timeProvider.GetUtcNow();
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        _caseCloseTimer ??= _timeProvider.CreateTimer(
            static state => ((WidgetStatusService)state!).OnCaseCloseDue(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _caseCloseTimer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    // Runs on a real timer thread, so like the other timers it catches what nothing else would, logs it with its code and
    // never lets it end the process.
    private void OnCaseCloseDue()
    {
        try
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                if (!_caseOpen.Expire(_timeProvider.GetUtcNow()))
                {
                    // A message came after the timer was set and moved the close time on.
                    ScheduleCaseCloseLocked();
                    return;
                }
            }

            PublishAndNotify();
        }
        catch (Exception ex)
        {
            _log.Error("Widget case close check failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }
    }

    // Must be called holding _gate. True once for every CaseOpened whose case has since closed (by the cadence, or a link
    // that was dropped or replaced), and never twice for one.
    private bool TakeCaseClosedLocked()
    {
        if (_caseOpenRaised && !_caseOpen.IsOpen)
        {
            _caseOpenRaised = false;
            return true;
        }

        return false;
    }

    // Must be called holding _gate. Merges one decoded reading of the linked set into what is shown.
    private void ApplyDecodedReadingLocked(DecodedReading reading, ProximityDecodeTable table, DateTimeOffset at)
    {
        _left = MergePart(_left, reading.Left);
        _right = MergePart(_right, reading.Right);
        _case = MergePart(_case, reading.Case);
        _lastChosenAt = at;

        if (reading.Left.InEar is bool li)
        {
            _lastLeftInEar = li;
        }

        if (reading.Right.InEar is bool ri)
        {
            _lastRightInEar = ri;
        }

        if (reading.Left.InEar is not null || reading.Right.InEar is not null)
        {
            _earReadAt = at;
        }

        // A lid bit is only ever in a test's table (none is documented): it is shown in the snapshot and nothing acts on it.
        // Opens come from CaseOpenTracker alone.
        if (table.LidOpenBit is not null && reading.LidOpen is bool lidOpen)
        {
            _lidOpenBitSeen = true;
            _lastLidOpenState = lidOpen;
        }
    }

    private static PartReading MergePart(PartReading previous, PartReading incoming) =>
        incoming.Percent is not null ? incoming : previous;

    private void OnStopped(object? sender, AdvertisementSourceStopped stopped)
    {
        bool stale;
        bool ownStop = false;
        lock (_gate)
        {
            // A Stopped whose generation is behind the one this service last started already belongs to a
            // run that has been superseded (Stop then an immediate Start, most often, where the old run's
            // own Stopped(Success) can arrive after the new one is already under way). Nothing about the
            // shown watcher state or the retry schedule changes because of it. The sender must also still be
            // the exact source instance this service tracks: a fresh source built after the setting goes off
            // then on starts its own numbering over, decoupled from whatever instance it replaced, so a late
            // Stopped from that old, already-replaced instance is never treated as current purely because its
            // own number happens not to read as behind.
            stale = !ReferenceEquals(sender, _source) || stopped.Generation < _generation;
            if (!stale)
            {
                ownStop = _stopRequested;
                _stopRequested = false;
                _watcherState = WidgetWatcherState.Stopped;
                _watcherErrorCode = stopped.ErrorCode;
                _watcherErrorName = stopped.ErrorName;
            }
        }

        if (stale)
        {
            string message = "Widget watcher stopped: " + stopped.ErrorName + " (" + stopped.ErrorCode +
                "), generation " + stopped.Generation + ", superseded by a later start.";
            if (stopped.ErrorCode == 0)
            {
                _log.Info(message);
            }
            else
            {
                _log.Warn(message);
            }

            return;
        }

        _log.Warn("Widget watcher stopped: " + stopped.ErrorName + " (" + stopped.ErrorCode + ").");

        if (!ownStop)
        {
            ScheduleRetry();
            CompleteAnyRefresh(stopped.ErrorCode == AdvertisementSourceCodes.RadioNotAvailableCode
                ? BatteryRefreshOutcome.BluetoothOff
                : BatteryRefreshOutcome.NotListening);
        }

        PublishAndNotify();
    }

    private void ScheduleRetry()
    {
        lock (_gate)
        {
            ScheduleRetryLocked();
        }
    }

    // A retry queued before Suspend must not start the watcher again during suspend. Resume() starts it
    // directly and arms its own retry if that still fails, so there is nothing for a timer to do meanwhile.
    private void ScheduleRetryLocked()
    {
        if (_suspended)
        {
            return;
        }

        _retryDelay = _retryDelay <= TimeSpan.Zero ? WidgetTiming.WatcherRetryDelay : _retryDelay;
        ArmRetryTimerLocked();
    }

    private void ArmRetryTimerLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = _timeProvider.CreateTimer(static state => ((WidgetStatusService)state!).OnRetryDue(), this, _retryDelay, Timeout.InfiniteTimeSpan);
    }

    // Runs on a real timer thread when the real advertisement source is live: an unhandled exception there
    // (a COM property on the watcher throwing, for instance) would otherwise end the whole process, so the
    // entire attempt is caught. The raw code is logged, never swallowed, and the catch arms the next attempt
    // on the doubling schedule (the timer is one-shot, so it would otherwise stop trying for good).
    //
    // A retry that was queued before Suspend must not start the watcher during suspend. Suspend cancels the
    // timer under the same lock this checks _suspended and _source in, so whichever of the two runs first is
    // decided cleanly; if this callback had already fired and was only waiting on the lock, it now sees
    // _suspended true and does nothing.
    //
    // Start runs outside the lock, like every other call site does, so a Start blocked here (the real
    // watcher waiting on a slow COM call, say) cannot hold the lock a Received or Stopped callback also
    // needs.
    //
    // Internal, not private: disposing a real timer does not stop a callback already dequeued and running,
    // so a test proving the suspended check above closes that race calls this directly straight after
    // Suspend(), standing in for a callback that fired just before the timer was disposed.
    internal void OnRetryDue()
    {
        IAdvertisementSource? source;
        int generation;
        lock (_gate)
        {
            if (_source is null || _suspended)
            {
                return;
            }

            _generation++;
            generation = _generation;
            source = _source;
        }

        RereadPairedModelIfNone();
        try
        {
            StepOutcome step = source.Start(generation);
            bool stopWhatJustStarted = false;
            bool disposeWhatJustStarted = false;
            lock (_gate)
            {
                bool stillTracked = !_closed && ReferenceEquals(_source, source);
                if (!stillTracked)
                {
                    // Close, or the setting going off, can each run between source.Start() returning and this
                    // lock being retaken. Neither's own stop pass is guaranteed to still catch this exact
                    // source once it has moved _source on or nulled it: whatever this retry just (re)started
                    // must be stopped and disposed on its own, or nothing left tracking it ever will.
                    if (step.Ok)
                    {
                        stopWhatJustStarted = true;
                        disposeWhatJustStarted = true;
                    }
                }
                else
                {
                    ApplyStartStepLocked(step);
                    if (_watcherState == WidgetWatcherState.Started)
                    {
                        // Suspend can also run between source.Start() returning and this lock being retaken.
                        // The early check at the top of this method only catches a Suspend that landed before
                        // Start() was ever called; re-check here too, or a source that raced past that check
                        // is left running with the service believing it is idle.
                        if (_suspended || _stopRequested)
                        {
                            stopWhatJustStarted = true;
                            _watcherState = WidgetWatcherState.Stopped;
                        }
                        else
                        {
                            _retryDelay = TimeSpan.Zero;
                            CancelRetryLocked();
                        }
                    }
                    else
                    {
                        _retryDelay = Min(_retryDelay * 2, WidgetTiming.WatcherRetryLimit);
                        ArmRetryTimerLocked();
                    }
                }
            }

            if (stopWhatJustStarted)
            {
                RunStopOutsideLock(source, disposeSource: disposeWhatJustStarted);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Widget watcher retry failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);

            // The timer is one-shot, so nothing else would ever try again: arm the next attempt here, as any
            // other failed attempt does, unless something has since stopped, suspended or replaced the source.
            lock (_gate)
            {
                if (!_closed && !_suspended && !_stopRequested && ReferenceEquals(_source, source))
                {
                    _retryDelay = _retryDelay <= TimeSpan.Zero ? WidgetTiming.WatcherRetryDelay : Min(_retryDelay * 2, WidgetTiming.WatcherRetryLimit);
                    ArmRetryTimerLocked();
                }
            }

            return;
        }

        PublishAndNotify();
    }

    // Used by RefreshAsync: one attempt now, outside the doubling schedule. Suspended is checked the same
    // way OnRetryDue checks it: a manual refresh must not restart the watcher during suspend either.
    private void RetryStartNow()
    {
        IAdvertisementSource? source;
        int generation;
        lock (_gate)
        {
            if (_source is null || _suspended)
            {
                return;
            }

            _generation++;
            generation = _generation;
            source = _source;
        }

        RereadPairedModelIfNone();
        RunStartOutsideLock(source, generation);
    }

    private void CancelRetryLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void PublishAndNotify()
    {
        WidgetSnapshot snapshot;
        bool changed;
        bool caseClosed;
        DateTimeOffset now;
        LastReadingBook? marksToSave = null;
        lock (_gate)
        {
            if (_closed)
            {
                return; // nothing after Close raises Changed, whatever called this
            }

            now = _timeProvider.GetUtcNow();
            snapshot = BuildSnapshotLocked(now);
            changed = SnapshotChangedIgnoringCounters(_lastPublished, snapshot);
            _lastPublished = snapshot;
            caseClosed = TakeCaseClosedLocked();

            // An estimate a surface has shown since the last write is written, at most as often as a read time alone is.
            if (_lastReadingStore is not null && _highWater.Version != _savedMarksVersion &&
                (_lastSavedAt is not DateTimeOffset last || (now - last).Duration() >= LastReadingStore.ReadTimeWriteInterval))
            {
                marksToSave = TakeForSaveLocked(_book);
                _savedBook = _book;
                _lastSavedAt = now;
            }
        }

        if (marksToSave is not null)
        {
            _lastReadingStore!.Save(marksToSave);
        }

        if (changed)
        {
            _uiPost(() => Changed?.Invoke(this, EventArgs.Empty));
        }

        if (caseClosed)
        {
            _uiPost(() => CaseClosed?.Invoke(this, new CaseClosedEventArgs(now)));
        }
    }

    // Counters tick on almost every advertisement (a section counted, a chosen reading, ...), so comparing
    // the whole snapshot by record equality (which is what WidgetCounters, and Counters.UnknownForms as a
    // reference-equality list, would fall back to) would raise Changed constantly even when nothing the UI
    // shows moved. Everything the UI actually displays is compared; Counters is deliberately left out, since
    // it is behind the test window's own technical-details toggle and follows the counters log line's own
    // once-a-minute cadence, not every advert.
    private static bool SnapshotChangedIgnoringCounters(WidgetSnapshot? previous, WidgetSnapshot current)
    {
        if (previous is null)
        {
            return true;
        }

        return previous.Where != current.Where ||
            previous.Left != current.Left ||
            previous.Right != current.Right ||
            previous.Case != current.Case ||
            previous.BatteryReadAt != current.BatteryReadAt ||
            previous.EarReadAt != current.EarReadAt ||
            previous.LidOpen != current.LidOpen ||
            previous.Watcher != current.Watcher ||
            previous.WatcherErrorCode != current.WatcherErrorCode ||
            previous.WatcherErrorName != current.WatcherErrorName ||
            previous.Headset != current.Headset ||
            previous.Selection != current.Selection ||
            !ReferenceEquals(previous.LastReadings, current.LastReadings) ||
            previous.PairedModel != current.PairedModel ||
            previous.AutoPauseAvailable != current.AutoPauseAvailable;
    }

    private bool IsFreshLocked(DateTimeOffset now) =>
        _lastChosenAt is DateTimeOffset at && now - at <= WidgetTiming.EarFreshWindow;

    private AirPodsWhere ComputeWhereLocked(bool fresh)
    {
        if (_thisPcActive)
        {
            return AirPodsWhere.ThisPc;
        }

        if (!fresh || (_lastLeftInEar is null && _lastRightInEar is null))
        {
            return AirPodsWhere.Unknown;
        }

        bool anyInEar = _lastLeftInEar == true || _lastRightInEar == true;
        return anyInEar ? AirPodsWhere.Elsewhere : AirPodsWhere.NotInUse;
    }

    private WidgetSnapshot BuildSnapshotLocked(DateTimeOffset now)
    {
        bool fresh = IsFreshLocked(now);
        AirPodsWhere where = ComputeWhereLocked(fresh);
        DateTimeOffset? earReadAt = fresh ? _earReadAt : null;
        DateTimeOffset? batteryReadAt = NewestReadAt(_left, _right, _case);
        bool autoPauseAvailable = _table.LeftInEarBit is not null || _table.RightInEarBit is not null;

        // Per-bud InEar is only as good as the reading it came from. Percent, Charging and ReadAt stand whatever
        // the age (BatteryFreshness says how old a value may be and still be shown as current); InEar is cleared
        // once the reading is no longer fresh, whatever the table sets, so a stale reading never keeps reporting a
        // bud as in or out of the ear.
        PartReading left = fresh ? _left : _left with { InEar = null };
        PartReading right = fresh ? _right : _right with { InEar = null };
        if (_estimateClock is null || now > _estimateClock)
        {
            _estimateClock = now;
        }

        return new WidgetSnapshot(
            where, left, right, _case, batteryReadAt, earReadAt, _lidOpenBitSeen ? _lastLidOpenState : null,
            _watcherState, _watcherErrorCode, _watcherErrorName,
            autoPauseAvailable, BuildCountersLocked())
        {
            Headset = _headset,
            Selection = _selector.StateAt(now),
            LastReadings = _book,
            PairedModel = _selector.PairedModel,
            EstimateClock = _estimateClock,
            HighWater = _highWater,
        };
    }

    // When anything was last read: the newest of the parts' read times. A part the broadcast does not say (the case
    // while its lid is shut) keeps its own old time and is greyed by itself; it must not make a card whose buds were
    // just heard say the battery was read hours ago.
    private static DateTimeOffset? NewestReadAt(PartReading a, PartReading b, PartReading c)
    {
        DateTimeOffset? newest = null;
        foreach (PartReading part in new[] { a, b, c })
        {
            if (part.ReadAt is DateTimeOffset at && (newest is null || at > newest))
            {
                newest = at;
            }
        }

        return newest;
    }

    private WidgetCounters BuildCountersLocked()
    {
        if (_cachedUnknownFormsVersion != _unknownFormsVersion)
        {
            var shapes = new (byte? Prefix, int Length, long Count)[_unknownForms.Count];
            int i = 0;
            foreach (KeyValuePair<(byte? Prefix, int Length), long> entry in _unknownForms)
            {
                shapes[i++] = (entry.Key.Prefix, entry.Key.Length, entry.Value);
            }

            _cachedUnknownForms = shapes;
            _cachedUnknownFormsVersion = _unknownFormsVersion;
        }

        return new WidgetCounters(
            Interlocked.Read(ref _allSections),
            Interlocked.Read(ref _appleSections),
            Interlocked.Read(ref _otherCompanySections),
            Interlocked.Read(ref _proximityItems),
            Interlocked.Read(ref _okForm),
            Interlocked.Read(ref _truncated),
            Interlocked.Read(ref _unknownForm),
            Interlocked.Read(ref _modelMismatch),
            Interlocked.Read(ref _colourMismatch),
            Interlocked.Read(ref _otherSet),
            Interlocked.Read(ref _chosen),
            Interlocked.Read(ref _noPairedModel),
            Interlocked.Read(ref _budOrderDisagree),
            Interlocked.Read(ref _switches),
            _setsInRange,
            _cachedUnknownForms)
        {
            Links = Interlocked.Read(ref _links),
            Followed = Interlocked.Read(ref _followed),
            Drops = Interlocked.Read(ref _drops),
        };
    }

    // Once a minute while anything changed since the last one, logs the counters as numbers, the
    // unknown-form shapes as prefix and length (never the bytes themselves), and the watcher state. Compared
    // field by field rather than with WidgetCounters' own record equality, since UnknownForms is a fresh
    // array on every read and would never compare equal to itself by reference.
    // Also runs on a real timer thread: the same boundary as OnRetryDue, so nothing here can end the process
    // either.
    private void OnCountersLogDue()
    {
        try
        {
            WidgetCounters current;
            WidgetWatcherState watcherState;
            lock (_gate)
            {
                current = BuildCountersLocked();
                watcherState = _watcherState;
                if (_lastLoggedCounters is WidgetCounters last && watcherState == _lastLoggedWatcherState && CountersEqual(current, last))
                {
                    return;
                }

                _lastLoggedCounters = current;
                _lastLoggedWatcherState = watcherState;
            }

            _log.Info(FormatCountersLine(current, watcherState));
        }
        catch (Exception ex)
        {
            _log.Error("Widget counters logging failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }
    }

    private static bool CountersEqual(WidgetCounters a, WidgetCounters b) =>
        a.AllSections == b.AllSections &&
        a.AppleSections == b.AppleSections &&
        a.OtherCompanySections == b.OtherCompanySections &&
        a.ProximityItems == b.ProximityItems &&
        a.OkForm == b.OkForm &&
        a.Truncated == b.Truncated &&
        a.UnknownForm == b.UnknownForm &&
        a.ModelMismatch == b.ModelMismatch &&
        a.ColourMismatch == b.ColourMismatch &&
        a.OtherSet == b.OtherSet &&
        a.Chosen == b.Chosen &&
        a.NoPairedModel == b.NoPairedModel &&
        a.BudOrderDisagree == b.BudOrderDisagree &&
        a.Switches == b.Switches &&
        a.Links == b.Links &&
        a.Followed == b.Followed &&
        a.Drops == b.Drops &&
        a.Sets == b.Sets &&
        a.UnknownForms.SequenceEqual(b.UnknownForms);

    // Numbers and shapes only: prefix and length describe an unknown form's shape, never its bytes. The first
    // seven fields keep their names and order: the live test reads them.
    private static string FormatCountersLine(WidgetCounters c, WidgetWatcherState watcherState)
    {
        string shapes = string.Join(
            ",",
            c.UnknownForms.Select(s => "(prefix=" + (s.Prefix?.ToString("X2") ?? "none") + " length=" + s.Length + " count=" + s.Count + ")"));

        return "Widget counters: watcher=" + watcherState +
            " allSections=" + c.AllSections +
            " apple=" + c.AppleSections +
            " other=" + c.OtherCompanySections +
            " items=" + c.ProximityItems +
            " ok=" + c.OkForm +
            " truncated=" + c.Truncated +
            " unknownForm=" + c.UnknownForm +
            " modelMismatch=" + c.ModelMismatch +
            " colourMismatch=" + c.ColourMismatch +
            " otherSet=" + c.OtherSet +
            " chosen=" + c.Chosen +
            " noPairedModel=" + c.NoPairedModel +
            " budOrderDisagree=" + c.BudOrderDisagree +
            " switches=" + c.Switches +
            " links=" + c.Links +
            " followed=" + c.Followed +
            " drops=" + c.Drops +
            " sets=" + c.Sets +
            " unknownFormShapes=[" + shapes + "]";
    }
}
