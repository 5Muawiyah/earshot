using Earshot.App;
using Earshot.Contracts;

namespace Earshot.Widget;

// Owns the advertisement source's lifetime and turns what it receives into the one snapshot the UI reads.
// Parsing runs on the callback thread the source calls back on; the ownership rule, the decoder, the
// snapshot and the events run after a post through uiPost, so every state change the UI can see happens on
// one thread. Counters are Interlocked so they can always be read without that post.
//
// Start, Suspend, Resume and Close are not on IWidgetStatus: they are called directly by whatever wires the
// widget into the tray (out of scope here), the way the device monitor's own Start is called once and its
// suspend/resume handling sits in the tray's power-event plumbing.
internal sealed class WidgetStatusService : IWidgetStatus, IDisposable
{
    private readonly Func<IAdvertisementSource> _sourceFactory;
    private readonly ClaimStore _claimStore;
    private readonly ISettingsStore _settings;
    private readonly IDeviceMonitor _deviceMonitor;
    private readonly Func<BootBlockStatus?> _blockStatus;
    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _timeProvider;

    // Reads what the owner's own set-up records have proved. In production this is the proof store's table,
    // which only moves when a set-up is completed; there is no constant to read. Tests supply a fixed table
    // so the proved paths are exercised directly.
    private readonly Func<ProximityDecodeTable> _decodeTable;

    // Whether the AirPods were observed to keep broadcasting while this PC plays to them: true once observed,
    // null before. In production the proof store's observation.
    private readonly Func<bool?> _broadcastsWhilePlaying;

    // Where a set-up's record and proof are kept. Null only in tests that never complete a set-up.
    private readonly DecodeProofStore? _proof;

    // True while the newest set-up saw only forms the parser does not read and nothing has been claimed since.
    private bool _setupCouldNotRead;

    private readonly Lock _gate = new();
    private readonly Dictionary<(byte? Prefix, int Length), long> _unknownForms = new();

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
    private WidgetCounters? _lastLoggedCounters;
    private WidgetWatcherState? _lastLoggedWatcherState;

    private long _allSections, _appleSections, _otherCompanySections, _proximityItems;
    private long _okForm, _truncated, _unknownForm;
    private long _owned, _noClaim, _modelOrColourMismatch;
    private long _signalBelowThreshold, _nibbleOrderMismatch, _batteryUnreadable, _batteryInconsistent;

    // Set once a NibbleOrderMismatch has been logged, so a whole run of adverts against a stale claim
    // produces one line, not one per advert; cleared whenever the claim changes (a redone claim, or one
    // forgotten), since either might fix or remove the mismatch.
    private bool _nibbleOrderMismatchLogged;

    // Everything below is read and written only while holding _gate, so a ClaimAsync/ForgetClaim call (from
    // whatever thread the owner's UI runs on) and a Received/Stopped callback never race each other.
    private PartReading _left = PartReading.Unknown;
    private PartReading _right = PartReading.Unknown;
    private PartReading _case = PartReading.Unknown;
    private bool? _lastLeftInEar;
    private bool? _lastRightInEar;
    private DateTimeOffset? _earReadAt;
    private DateTimeOffset? _lastOwnedAt;
    private bool _lidOpenBitSeen;
    private bool _lastLidOpenState;
    private int? _lastLidCounter;
    private bool _thisPcActive;
    private WidgetClaim? _claim;
    private WidgetWatcherState _watcherState = WidgetWatcherState.NotStarted;
    private int? _watcherErrorCode;
    private string? _watcherErrorName;
    private WidgetSnapshot? _lastPublished;

    // Production entry point: the decode table and the broadcast observation are always the proof store's own,
    // so nothing composing this service can wire up a different table.
    public WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ClaimStore claimStore,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider,
        DecodeProofStore proof)
        : this(
            sourceFactory, claimStore, settings, deviceMonitor, blockStatus, log, uiPost, timeProvider,
            RequireProof(proof).ReadTable, proof.ReadBroadcast, proof)
    {
    }

    private static DecodeProofStore RequireProof(DecodeProofStore? proof) =>
        proof ?? throw new ArgumentNullException(nameof(proof));

    // Test-only: supplies the decode table and the broadcast observation directly, so a test can exercise the
    // proved paths without a set-up having proved anything. proof is needed only by a test that completes a
    // set-up or counts owned messages while this PC plays.
    internal WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ClaimStore claimStore,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider,
        Func<ProximityDecodeTable> decodeTable,
        Func<bool?>? broadcastsWhilePlaying = null,
        DecodeProofStore? proof = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(claimStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceMonitor);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(decodeTable);

        _sourceFactory = sourceFactory;
        _claimStore = claimStore;
        _broadcastsWhilePlaying = broadcastsWhilePlaying ?? (static () => null);
        _proof = proof;
        _settings = settings;
        _deviceMonitor = deviceMonitor;
        _blockStatus = blockStatus;
        _decodeTable = decodeTable;
        _log = log;
        _uiPost = uiPost;
        _timeProvider = timeProvider;
        _claim = claimStore.Current;
        _setupCouldNotRead = _claim is null && proof?.Newest is { ListenStatus: BatterySetupListenStatus.ShortFormOnly };
    }

    public event EventHandler? Changed;

    public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

    // Raised for every owned reading (an Owned verdict): the seam LowBatteryAlertService feeds
    // from, and a later auto-pause step will reuse. See ApplyOwnedMessage for exactly where.
    public event EventHandler<OwnedReadingEventArgs>? OwnedReadingApplied;

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

    // Whether the AirPods were observed to keep broadcasting while this PC plays to them: true once observed,
    // null before. What auto-pause's gate reads.
    public bool? BroadcastObserved() => _broadcastsWhilePlaying();

    // True while the watcher runs: a set-up listens through it, so with it stopped the trigger is disabled
    // rather than left to fail after the owner has opened his case.
    public bool SetupAvailable
    {
        get
        {
            lock (_gate)
            {
                return !_closed && _watcherState == WidgetWatcherState.Started;
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
        lock (_gate)
        {
            if (_started || _closed)
            {
                return;
            }

            _started = true;
            RecomputeThisPcLocked(_deviceMonitor.Current);
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
        }

        if (sourceToStart is not null)
        {
            // Unlike every other call site of RunStartOutsideLock, the very first Start() never publishes:
            // nothing has anything to compare its first snapshot against yet, and it matches what the
            // original, single-lock version of this method did (a caller watching uiPost's own queue,
            // ChangedAndCaseOpenedAreRaisedThroughUiPost, pins it).
            RunStartOutsideLock(sourceToStart, generation, publish: false);
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

    // Idempotent, and final: once closed, nothing on this service saves the claim or raises Changed or
    // CaseOpened again, whatever calls it (or an in-flight callback that was already dispatched before this
    // ran) tries next.
    public void Close()
    {
        IAdvertisementSource? sourceToStop;
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

            _deviceMonitor.SnapshotChanged -= OnDeviceSnapshotChanged;
            sourceToStop = BeginStopLocked();
        }

        if (sourceToStop is not null)
        {
            RunStopOutsideLock(sourceToStop, disposeSource: true);
        }
    }

    public void Dispose() => Close();

    public async Task<BatterySetupListen> ListenForSetupAsync(CancellationToken ct)
    {
        IAdvertisementSource? source;
        lock (_gate)
        {
            source = _closed ? null : _source;
        }

        if (source is null)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _log.Warn("Battery set-up: the watcher is not running, so nothing was listened for.");
            return new BatterySetupListen(
                BatterySetupListenStatus.WatcherNotStarted, WidgetCopy.SetupBluetoothOff, Candidate: null, [], now, now, 0, 0);
        }

        return await BatterySetupFlow.ListenAsync(source, _timeProvider, WidgetTiming.SetupListenWindow, _log, ct).ConfigureAwait(false);
    }

    // The owner has answered step 2. The record is written and proof updated first, so the claim's
    // NibblesAreNamedOrder matches the table the run-time rule will use: the other order would make the very
    // first reading after the proving set-up a NibbleOrderMismatch.
    public BatterySetupResult CompleteSetup(BatterySetupListen listen, BatterySetupPicks picks)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(picks);
        if (_proof is not { } proof)
        {
            throw new InvalidOperationException("This service was built without a proof store, so a set-up cannot be completed.");
        }

        if (listen.Status is not (BatterySetupListenStatus.Found or BatterySetupListenStatus.ShortFormOnly) || listen.Candidate is not { } candidate)
        {
            throw new ArgumentException("Only a listen that found the owner's case can be completed.", nameof(listen));
        }

        if (!picks.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(picks), "Picks are 0 to 100 in steps of 10.");
        }

        var record = new BatterySetupRecord(
            BatterySetupRecord.CurrentSchemaVersion, listen.StartedAtUtc, listen.EndedAtUtc, listen.Status, AppVersion(),
            listen.AppleSectionsSeen, listen.ProximityItemsSeen, candidate, listen.OtherSenders, picks);

        string? saved = proof.Setups.Save(record);
        _log.Info(
            "Battery set-up saved: " + (saved ?? record.FileName) + "; picks L " + picks.Left + " R " + picks.Right + " Case " + picks.Case +
            ", charging " + YesNo(picks.LeftCharging) + "/" + YesNo(picks.RightCharging) + "/" + YesNo(picks.CaseCharging) + ".");

        proof.AddRecord(record);
        ProximityDecodeTable table = proof.Table;
        BudOrderEvidence evidence = DecodeProof.Discriminate(record);
        DecodeProofResult result = proof.Result;

        if (candidate.LastOkMessage is not ProximityMessage message)
        {
            lock (_gate)
            {
                _setupCouldNotRead = _claim is null;
            }

            PublishAndNotify();
            return new BatterySetupResult(BatterySetupResultStatus.CouldNotRead, record.FileName, result);
        }

        // A set-up always supersedes the claim before it: the store keeps only the newer of two claims, so a
        // clock that reads earlier than the old claim's date must not make the new one lose.
        DateTimeOffset claimedAt = _timeProvider.GetUtcNow();
        if (_claimStore.Current is { } previousClaim && claimedAt <= previousClaim.ClaimedAtUtc)
        {
            claimedAt = previousClaim.ClaimedAtUtc + TimeSpan.FromSeconds(1);
        }

        var claim = new WidgetClaim(
            SchemaVersion: WidgetClaim.CurrentSchemaVersion,
            ModelHigh: message.ModelHigh,
            ModelLow: message.ModelLow,
            Colour: message.Colour,
            SignalThresholdDbm: candidate.ThresholdDbm,
            SignalMinDbm: candidate.RssiMin,
            SignalMedianDbm: candidate.RssiMedian,
            SignalMaxDbm: candidate.RssiMax,
            SignalSamples: candidate.Messages,
            SetupRecord: record.FileName,
            ClaimedAtUtc: claimedAt,
            Last: OwnedBattery.FromMessage(message, table, previous: null, at: listen.EndedAtUtc),
            NibblesAreNamedOrder: table.HighNibbleIsRight is not null);

        bool closed;
        bool nothingShown = false;
        lock (_gate)
        {
            closed = _closed;
            if (!closed)
            {
                _claimStore.Save(claim);
                _claim = claim;
                _setupCouldNotRead = false;
                ResetReadingsLocked();
            }
        }

        if (!closed)
        {
            _log.Info("AirPods claimed from set-up " + record.FileName + ": threshold " + candidate.ThresholdDbm + " dBm.");

            // The first reading is applied at once, so the card has something to show: the case, or all three
            // once the order is proved. It goes through the same rule as every later one.
            ApplyOwnedMessage(message, candidate.RssiMedian, listen.EndedAtUtc);
            lock (_gate)
            {
                // A set-up that leaves nothing to show (the case unreadable or doubted, the buds unproved) is
                // said to have read nothing, never "battery set up".
                nothingShown = _left.Percent is null && _right.Percent is null && _case.Percent is null;
                _setupCouldNotRead = nothingShown;
            }

            PublishAndNotify();
        }

        if (nothingShown)
        {
            return new BatterySetupResult(BatterySetupResultStatus.CouldNotRead, record.FileName, result);
        }

        BatterySetupResultStatus status = table.HighNibbleIsRight is not null
            ? BatterySetupResultStatus.BatterySetUp
            : evidence.HighNibbleIsRight is null
                ? BatterySetupResultStatus.CaseSetUpBudsSame
                : BatterySetupResultStatus.CaseSetUp;
        return new BatterySetupResult(status, record.FileName, result);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    // The build's own version, without the source revision suffix a build may append.
    private static string AppVersion()
    {
        string? version = typeof(WidgetStatusService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return "unknown";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    public void ForgetClaim()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _claimStore.ForgetClaim();
            _claim = null;
            ResetReadingsLocked();
        }

        PublishAndNotify();
    }

    // A redone claim is a different set of AirPods as far as the widget knows (the owner was told to
    // open his case for it), and forgetting a claim starts over; neither may keep the old battery, ear or
    // lid state around to be shown against, or compared for consistency by, whatever is claimed next.
    private void ResetReadingsLocked()
    {
        _left = PartReading.Unknown;
        _right = PartReading.Unknown;
        _case = PartReading.Unknown;
        _lastLeftInEar = null;
        _lastRightInEar = null;
        _earReadAt = null;
        _lastOwnedAt = null;
        _lidOpenBitSeen = false;
        _lastLidOpenState = false;
        _lastLidCounter = null;
        _nibbleOrderMismatchLogged = false;
    }

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
            _watcherErrorCode = step.Code;
            _watcherErrorName = step.CodeName;
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
            RunStartOutsideLock(sourceToStart, generation); // publishes itself
            return;
        }

        if (sourceToStop is not null)
        {
            RunStopOutsideLock(sourceToStop, disposeSource: true);
        }

        if (changed)
        {
            PublishAndNotify();
        }
    }

    private void OnDeviceSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        lock (_gate)
        {
            RecomputeThisPcLocked(e.Snapshot);
        }

        PublishAndNotify();
    }

    private void RecomputeThisPcLocked(DeviceSnapshot snapshot)
    {
        Guid watched = CoordinatorRules.WatchedContainer(_blockStatus(), _settings.Current, snapshot);
        _thisPcActive = CoordinatorRules.RenderOf(snapshot, watched) == RenderState.Active;
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
        _uiPost(() => HandleParsedOnUiThread(parse, sample.Rssi, at));
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

    private void HandleParsedOnUiThread(ProximityParse parse, sbyte rssi, DateTimeOffset at)
    {
        if (parse.Status == ProximityParseStatus.Ok && parse.Message is ProximityMessage message)
        {
            ApplyOwnedMessage(message, rssi, at);
        }

        PublishAndNotify();
    }

    private void ApplyOwnedMessage(ProximityMessage message, sbyte rssi, DateTimeOffset at)
    {
        ProximityDecodeTable table = _decodeTable();
        bool caseOpenedEdge = false;
        DateTimeOffset caseOpenedAt = at;
        DecodedReading? ownedReading = null;

        // 11 to 14 are a shape neither permitted source describes at all (unlike the documented 0xF
        // "unknown"), so it is logged as the form drifting rather than treated the same as ordinary unknown.
        // Never the nibble value itself: only that one was out of range.
        if (BatteryNibble.IsOutOfRange((message.BatteryA >> 4) & 0x0F) ||
            BatteryNibble.IsOutOfRange(message.BatteryA & 0x0F) ||
            BatteryNibble.IsOutOfRange(message.BatteryB & 0x0F))
        {
            _log.Warn("Widget: a battery nibble read 11 to 14, a shape the documented form does not describe: the form may have drifted.");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return; // nothing after Close saves the claim or raises CaseOpened
            }

            // Owner decision, 2026-09-27 ("same checks always"): a live connection to this PC used to waive
            // battery consistency for one candidate. It no longer does; the same checks run every time,
            // whether or not this PC renders to the AirPods, so there is nothing here to read from Core Audio
            // and no sender tag to track across messages any more.
            var input = new OwnershipInput(
                new ProximityParse(ProximityParseStatus.Ok, message, null, null, 1, Array.Empty<byte>()),
                rssi, _claim, table, at);
            OwnershipResult result = OwnershipRule.Evaluate(input);

            switch (result.Verdict)
            {
                case OwnershipVerdict.Owned:
                    Interlocked.Increment(ref _owned);

                    // The one place this run watches for the AirPods still broadcasting while this PC plays to
                    // them: what auto-pause needs to know before it may ever act.
                    if (_thisPcActive)
                    {
                        _proof?.NoteOwnedWhileThisPcRenders(at);
                    }

                    break;
                case OwnershipVerdict.NoClaim:
                    Interlocked.Increment(ref _noClaim);
                    return;
                case OwnershipVerdict.ModelOrColourMismatch:
                    Interlocked.Increment(ref _modelOrColourMismatch);
                    return;
                case OwnershipVerdict.SignalBelowThreshold:
                    Interlocked.Increment(ref _signalBelowThreshold);
                    return;
                case OwnershipVerdict.NibbleOrderMismatch:
                    Interlocked.Increment(ref _nibbleOrderMismatch);
                    if (!_nibbleOrderMismatchLogged)
                    {
                        _nibbleOrderMismatchLogged = true;
                        _log.Warn(
                            "Widget: the claim's battery nibble order no longer matches the decode table's, so nothing is shown until the claim is redone.");
                    }

                    return;
                case OwnershipVerdict.BatteryUnreadable:
                    Interlocked.Increment(ref _batteryUnreadable);
                    return;
                case OwnershipVerdict.BatteryInconsistent:
                    Interlocked.Increment(ref _batteryInconsistent);
                    return;
                default:
                    return;
            }

            if (result.UpdatedLast is OwnedBattery updated && _claim is not null)
            {
                // Every owned advertisement carries a fresh read time, so Last as a whole (with its AtUtc)
                // never equals what is on disk two adverts running: saving whenever Last changed would save
                // on every single one. Only the nibbles decide whether the disk actually needs touching; the
                // read time itself simply moves on in memory and rides along with whatever save a later
                // value change makes.
                bool valueChanged = updated.NibbleHigh != _claim.Last.NibbleHigh ||
                    updated.NibbleLow != _claim.Last.NibbleLow ||
                    updated.Case != _claim.Last.Case;
                _claim = _claim with { Last = updated };
                if (valueChanged)
                {
                    _claimStore.Save(_claim);
                }
            }

            DecodedReading reading = ProximityDecoder.Decode(message, table, at);
            caseOpenedEdge = ApplyDecodedReadingLocked(reading, table, at);
            ownedReading = reading;
        }

        if (caseOpenedEdge)
        {
            _uiPost(() => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(caseOpenedAt)));
        }

        // Every verdict other than Owned returned before this point (inside the lock above), so
        // ownedReading being set at all already means Owned: no verdict check is repeated here.
        if (ownedReading is DecodedReading applied)
        {
            _uiPost(() => OwnedReadingApplied?.Invoke(this, new OwnedReadingEventArgs(applied, at)));
        }
    }

    // Must be called holding _gate. Returns true when this reading raised the lid's rising edge or a new
    // counter value, for CaseOpened.
    private bool ApplyDecodedReadingLocked(DecodedReading reading, ProximityDecodeTable table, DateTimeOffset at)
    {
        _left = MergePart(_left, reading.Left);
        _right = MergePart(_right, reading.Right);
        _case = MergePart(_case, reading.Case);
        _lastOwnedAt = at;

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

        bool caseOpenedEdge = false;
        if (table.LidOpenBit is not null && reading.LidOpen is bool lidOpen)
        {
            _lidOpenBitSeen = true;
            caseOpenedEdge = lidOpen && !_lastLidOpenState;
            _lastLidOpenState = lidOpen;
        }
        else if (table.LidCounterMask is not null && reading.LidCounter is int counter)
        {
            // Unlike the lid-bit path (an assumed-false baseline, so a true first reading is a genuine rising
            // edge), there is no "previous" counter value to compare on the very first owned reading: it only
            // establishes the baseline, and never raises CaseOpened by itself.
            caseOpenedEdge = _lastLidCounter is int last && last != counter;
            _lastLidCounter = counter;
        }

        return caseOpenedEdge;
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
    // entire attempt is caught. The raw code is logged, never swallowed, and the retry is left armed at its
    // current delay so the doubling schedule simply tries again rather than stopping forever.
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
        lock (_gate)
        {
            if (_closed)
            {
                return; // nothing after Close raises Changed, whatever called this
            }

            snapshot = BuildSnapshotLocked(_timeProvider.GetUtcNow());
            changed = SnapshotChangedIgnoringCounters(_lastPublished, snapshot);
            _lastPublished = snapshot;
        }

        if (changed)
        {
            _uiPost(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    // Counters tick on almost every advertisement (a section counted, an owned reading, ...), so comparing
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
            previous.ClaimExists != current.ClaimExists ||
            previous.SetupCouldNotRead != current.SetupCouldNotRead ||
            previous.AutoPauseAvailable != current.AutoPauseAvailable;
    }

    private bool IsFreshLocked(DateTimeOffset now) =>
        _lastOwnedAt is DateTimeOffset at && now - at <= WidgetTiming.EarFreshWindow;

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
        DateTimeOffset? batteryReadAt = OldestReadAt(_left, _right, _case);
        ProximityDecodeTable table = _decodeTable();
        bool? lidOpen = _lidOpenBitSeen ? _lastLidOpenState : null;
        bool autoPauseAvailable = _broadcastsWhilePlaying() == true &&
            (table.LeftInEarBit is not null || table.RightInEarBit is not null);

        // Per-bud InEar is only as good as the reading it came from. Battery never expires, so Percent,
        // Charging and ReadAt stand whatever the age; InEar is cleared once the reading is no longer fresh,
        // whatever the table proves, so a stale reading never keeps reporting a bud as in or out of the ear.
        PartReading left = fresh ? _left : _left with { InEar = null };
        PartReading right = fresh ? _right : _right with { InEar = null };

        return new WidgetSnapshot(
            where, left, right, _case, batteryReadAt, earReadAt, lidOpen,
            _watcherState, _watcherErrorCode, _watcherErrorName,
            _claim is not null, autoPauseAvailable, BuildCountersLocked())
        {
            SetupCouldNotRead = _setupCouldNotRead,
        };
    }

    private static DateTimeOffset? OldestReadAt(PartReading a, PartReading b, PartReading c)
    {
        DateTimeOffset? oldest = null;
        foreach (PartReading part in new[] { a, b, c })
        {
            if (part.ReadAt is DateTimeOffset at && (oldest is null || at < oldest))
            {
                oldest = at;
            }
        }

        return oldest;
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
            Interlocked.Read(ref _owned),
            Interlocked.Read(ref _noClaim),
            Interlocked.Read(ref _modelOrColourMismatch),
            Interlocked.Read(ref _signalBelowThreshold),
            Interlocked.Read(ref _nibbleOrderMismatch),
            Interlocked.Read(ref _batteryUnreadable),
            Interlocked.Read(ref _batteryInconsistent),
            _cachedUnknownForms);
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
        a.Owned == b.Owned &&
        a.NoClaim == b.NoClaim &&
        a.ModelOrColourMismatch == b.ModelOrColourMismatch &&
        a.SignalBelowThreshold == b.SignalBelowThreshold &&
        a.NibbleOrderMismatch == b.NibbleOrderMismatch &&
        a.BatteryUnreadable == b.BatteryUnreadable &&
        a.BatteryInconsistent == b.BatteryInconsistent &&
        a.UnknownForms.SequenceEqual(b.UnknownForms);

    // Numbers and shapes only: prefix and length describe an unknown form's shape, never its bytes.
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
            " owned=" + c.Owned +
            " noClaim=" + c.NoClaim +
            " modelOrColourMismatch=" + c.ModelOrColourMismatch +
            " signalBelowThreshold=" + c.SignalBelowThreshold +
            " nibbleOrderMismatch=" + c.NibbleOrderMismatch +
            " batteryUnreadable=" + c.BatteryUnreadable +
            " batteryInconsistent=" + c.BatteryInconsistent +
            " unknownFormShapes=[" + shapes + "]";
    }
}
