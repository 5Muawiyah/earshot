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
    private readonly ClaimFlow _claimFlow;
    private readonly ISettingsStore _settings;
    private readonly IDeviceMonitor _deviceMonitor;
    private readonly Func<BootBlockStatus?> _blockStatus;
    private readonly ILog _log;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _timeProvider;

    // Reads phase 0's proved shape. In production this is () => ProximityDecodeTable.Current, which stays
    // Unproved until phase 0 edits it; tests supply a fixed table so the proved paths are exercised without
    // that static ever holding anything but its shipped default.
    private readonly Func<ProximityDecodeTable> _decodeTable;

    // Reads phase 0's proved signal threshold for ClaimAsync. In production this is
    // () => WidgetDefaults.SignalThresholdDbm, which stays null until phase 0 edits it, so a claim can never
    // be made from a guessed threshold; tests supply a fixed value so a claim can actually be exercised.
    private readonly Func<sbyte?> _claimThreshold;

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
    private ITimer? _retryTimer;
    private TimeSpan _retryDelay;
    private ITimer? _countersLogTimer;
    private WidgetCounters? _lastLoggedCounters;
    private WidgetWatcherState? _lastLoggedWatcherState;

    private long _allSections, _appleSections, _otherCompanySections, _proximityItems;
    private long _okForm, _truncated, _unknownForm;
    private long _owned, _noClaim, _modelOrColourMismatch;
    private long _signalBelowThreshold, _batteryUnreadable, _batteryInconsistent;

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

    // Production entry point: always reads phase 0's proved shape from ProximityDecodeTable.Current itself,
    // so nothing composing this service can accidentally wire up a different table (M4; acceptance 17).
    public WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ClaimStore claimStore,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider)
        : this(
            sourceFactory, claimStore, settings, deviceMonitor, blockStatus, log, uiPost, timeProvider,
            static () => ProximityDecodeTable.Current, static () => WidgetDefaults.SignalThresholdDbm)
    {
    }

    // Test-only: supplies the decode table and the claim threshold directly, so a test can exercise the
    // proved paths, and can actually make a claim, without ProximityDecodeTable.Current or
    // WidgetDefaults.SignalThresholdDbm ever holding anything but their shipped Unproved/null defaults.
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
        Func<sbyte?>? claimThreshold = null)
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
        _claimFlow = new ClaimFlow(claimStore, log);
        _claimThreshold = claimThreshold ?? (static () => WidgetDefaults.SignalThresholdDbm);
        _settings = settings;
        _deviceMonitor = deviceMonitor;
        _blockStatus = blockStatus;
        _decodeTable = decodeTable;
        _log = log;
        _uiPost = uiPost;
        _timeProvider = timeProvider;
        _claim = claimStore.Current;
    }

    public event EventHandler? Changed;

    public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

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
                StartSourceLocked();
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
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_source is null || _closed)
            {
                return;
            }

            _stopRequested = true;
            CancelRetryLocked();
            StepOutcome step = _source.Stop();
            ApplyStopStepLocked(step);
            _watcherState = WidgetWatcherState.Stopped;
        }

        PublishAndNotify();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_source is null || !_settings.Current.Widget.Enabled || _closed)
            {
                return;
            }

            _stopRequested = false;
            StepOutcome step = _source.Start();
            ApplyStartStepLocked(step);
        }

        PublishAndNotify();
    }

    // Idempotent, and final: once closed, nothing on this service saves the claim or raises Changed or
    // CaseOpened again, whatever calls it (or an in-flight callback that was already dispatched before this
    // ran) tries next.
    public void Close()
    {
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
            StopSourceLocked(disposeSource: true);
        }
    }

    public void Dispose() => Close();

    public async Task<ClaimOutcome> ClaimAsync(CancellationToken ct)
    {
        IAdvertisementSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null)
        {
            return new ClaimOutcome(ClaimOutcomeStatus.WatcherNotStarted, "Bluetooth is off or the watcher stopped: see the log.", null);
        }

        // The internal overload, with the threshold and table read through the same injectable seams as the
        // rest of the service (M4/M7), so a test can actually drive a claim through without either static
        // default ever holding anything but what it ships with.
        ClaimOutcome outcome = await _claimFlow.RunAsync(
            source, _timeProvider, WidgetTiming.ClaimWindow, _claimThreshold(), _decodeTable(), ct).ConfigureAwait(false);
        if (outcome.Status == ClaimOutcomeStatus.Claimed)
        {
            bool closed;
            lock (_gate)
            {
                closed = _closed;
                if (!closed)
                {
                    _claim = outcome.Claim;
                    ResetReadingsLocked();
                }
            }

            if (!closed)
            {
                PublishAndNotify();
            }
        }

        return outcome;
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

    private void StartSourceLocked()
    {
        _source = _sourceFactory();
        _source.Received += OnReceived;
        _source.Stopped += OnStopped;
        _stopRequested = false;
        StepOutcome step = _source.Start();
        ApplyStartStepLocked(step);
    }

    private void ApplyStartStepLocked(StepOutcome step)
    {
        _watcherState = _source!.State == AdvertisementSourceState.Started ? WidgetWatcherState.Started : WidgetWatcherState.Stopped;
        _watcherErrorCode = null;
        _watcherErrorName = null;
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

    private void StopSourceLocked(bool disposeSource)
    {
        if (_source is null)
        {
            return;
        }

        _stopRequested = true;
        CancelRetryLocked();
        StepOutcome stopStep = _source.Stop();
        ApplyStopStepLocked(stopStep);
        _source.Received -= OnReceived;
        _source.Stopped -= OnStopped;
        if (disposeSource)
        {
            _source.Dispose();
            _source = null;
        }

        _watcherState = WidgetWatcherState.Off;
    }

    private void OnSettingsChanged(object? sender, EarshotSettings settings)
    {
        bool changed;
        lock (_gate)
        {
            bool enabled = settings.Widget.Enabled;
            if (enabled && _source is null)
            {
                StartSourceLocked();
                changed = true;
            }
            else if (!enabled && _source is not null)
            {
                StopSourceLocked(disposeSource: true);
                changed = true;
            }
            else
            {
                changed = false;
            }
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
            // battery consistency for one candidate (D6). It no longer does; the same checks run every time,
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
        }

        if (caseOpenedEdge)
        {
            _uiPost(() => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(caseOpenedAt)));
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
        bool ownStop;
        lock (_gate)
        {
            ownStop = _stopRequested;
            _stopRequested = false;
            _watcherState = WidgetWatcherState.Stopped;
            _watcherErrorCode = stopped.ErrorCode;
            _watcherErrorName = stopped.ErrorName;
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
            _retryDelay = _retryDelay <= TimeSpan.Zero ? WidgetTiming.WatcherRetryDelay : _retryDelay;
            ArmRetryTimerLocked();
        }
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
    private void OnRetryDue()
    {
        try
        {
            lock (_gate)
            {
                if (_source is null)
                {
                    return;
                }

                StepOutcome step = _source.Start();
                ApplyStartStepLocked(step);
                if (_watcherState == WidgetWatcherState.Started)
                {
                    _retryDelay = TimeSpan.Zero;
                    CancelRetryLocked();
                }
                else
                {
                    _retryDelay = Min(_retryDelay * 2, WidgetTiming.WatcherRetryLimit);
                    ArmRetryTimerLocked();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Widget watcher retry failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
            return;
        }

        PublishAndNotify();
    }

    // Used by RefreshAsync: one attempt now, outside the doubling schedule.
    private void RetryStartNow()
    {
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            StepOutcome step = _source.Start();
            ApplyStartStepLocked(step);
            if (_watcherState == WidgetWatcherState.Started)
            {
                _retryDelay = TimeSpan.Zero;
                CancelRetryLocked();
            }
        }

        PublishAndNotify();
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
        bool autoPauseAvailable = WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc == true &&
            (table.LeftInEarBit is not null || table.RightInEarBit is not null);

        // Per-bud InEar is only as good as the reading it came from. Battery never expires, so Percent,
        // Charging and ReadAt stand whatever the age; InEar is cleared once the reading is no longer fresh,
        // whatever the table proves, so a stale reading never keeps reporting a bud as in or out of the ear.
        PartReading left = fresh ? _left : _left with { InEar = null };
        PartReading right = fresh ? _right : _right with { InEar = null };

        return new WidgetSnapshot(
            where, left, right, _case, batteryReadAt, earReadAt, lidOpen,
            _watcherState, _watcherErrorCode, _watcherErrorName,
            _claim is not null, autoPauseAvailable, BuildCountersLocked());
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
            " batteryUnreadable=" + c.BatteryUnreadable +
            " batteryInconsistent=" + c.BatteryInconsistent +
            " unknownFormShapes=[" + shapes + "]";
    }
}
