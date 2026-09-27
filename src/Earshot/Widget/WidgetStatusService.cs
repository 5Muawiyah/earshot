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

    private readonly Lock _gate = new();
    private readonly Dictionary<(byte? Prefix, int Length), long> _unknownForms = new();

    // Distinct sender tags, matching the current claim's model and colour and clearing its signal threshold,
    // seen within WidgetTiming.LiveCandidateWindow. Used only to decide whether a live connection to this PC
    // confirms exactly one candidate (D6): two senders clearing the threshold in the window make it ambiguous.
    private readonly Dictionary<uint, DateTimeOffset> _liveCandidates = new();

    private IAdvertisementSource? _source;
    private bool _stopRequested;
    private bool _settingsHooked;
    private ITimer? _retryTimer;
    private TimeSpan _retryDelay;

    private long _allAdvertisements, _appleSections, _otherCompanySections, _proximityItems;
    private long _okForm, _truncated, _unknownForm;
    private long _owned, _ownedByLiveConnection, _noClaim, _modelOrColourMismatch;
    private long _signalBelowThreshold, _batteryUnreadable, _batteryInconsistent, _ambiguousCandidates;

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

    public WidgetStatusService(
        Func<IAdvertisementSource> sourceFactory,
        ClaimStore claimStore,
        ISettingsStore settings,
        IDeviceMonitor deviceMonitor,
        Func<BootBlockStatus?> blockStatus,
        ILog log,
        Action<Action> uiPost,
        TimeProvider timeProvider,
        Func<ProximityDecodeTable> decodeTable)
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
    // regardless, so turning the setting on later (TurningTheSettingOnStartsOne) still works.
    public void Start()
    {
        lock (_gate)
        {
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
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_source is null)
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
            if (_source is null || !_settings.Current.Widget.Enabled)
            {
                return;
            }

            _stopRequested = false;
            StepOutcome step = _source.Start();
            ApplyStartStepLocked(step);
        }

        PublishAndNotify();
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_settingsHooked)
            {
                _settings.Changed -= OnSettingsChanged;
                _settingsHooked = false;
            }

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

        ClaimOutcome outcome = await _claimFlow.RunAsync(source, _timeProvider, WidgetTiming.ClaimWindow, ct).ConfigureAwait(false);
        if (outcome.Status == ClaimOutcomeStatus.Claimed)
        {
            lock (_gate)
            {
                _claim = outcome.Claim;
                _liveCandidates.Clear();
            }

            PublishAndNotify();
        }

        return outcome;
    }

    public void ForgetClaim()
    {
        lock (_gate)
        {
            _claimStore.ForgetClaim();
            _claim = null;
            _liveCandidates.Clear();
        }

        PublishAndNotify();
    }

    // One immediate retry when the watcher is not running; the doubling timer keeps trying regardless.
    public Task RefreshAsync()
    {
        bool tryStart;
        lock (_gate)
        {
            tryStart = _source is not null && _source.State != AdvertisementSourceState.Started;
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

    // M3: every Stop step is logged with its code and detail too, not just Start's; at Warn when not ok, so
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

    // Runs on whatever thread the source calls back on (D8 in the widget's own design notes: parse here,
    // publish through uiPost). Every Apple 0x07 section, however it parsed, is posted on; anything else
    // (a different company, or Apple data with no 0x07 item at all) ends here.
    private void OnReceived(object? sender, AdvertisementSample sample)
    {
        Interlocked.Increment(ref _allAdvertisements);
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
        uint senderTag = sample.SenderTag;
        _uiPost(() => HandleParsedOnUiThread(parse, sample.Rssi, senderTag, at));
    }

    private void RecordUnknownForm(byte? prefix, int length)
    {
        lock (_gate)
        {
            var key = (prefix, length);
            if (_unknownForms.TryGetValue(key, out long count))
            {
                _unknownForms[key] = count + 1;
            }
            else if (_unknownForms.Count < WidgetCounters.MaxUnknownFormShapes)
            {
                _unknownForms[key] = 1;
            }
        }
    }

    private void HandleParsedOnUiThread(ProximityParse parse, sbyte rssi, uint senderTag, DateTimeOffset at)
    {
        if (parse.Status == ProximityParseStatus.Ok && parse.Message is ProximityMessage message)
        {
            ApplyOwnedMessage(message, rssi, senderTag, at);
        }

        PublishAndNotify();
    }

    private void ApplyOwnedMessage(ProximityMessage message, sbyte rssi, uint senderTag, DateTimeOffset at)
    {
        ProximityDecodeTable table = _decodeTable();
        bool caseOpenedEdge = false;
        DateTimeOffset caseOpenedAt = at;

        lock (_gate)
        {
            // A live connection to this PC is read from Core Audio alone. Because addresses rotate, the
            // owner's own set can briefly appear under two sender tags; the live waiver applies only when
            // exactly one distinct sender matching the claim has cleared the threshold within the candidate
            // window (D6, B1), so that count is tracked here rather than assumed to be one.
            bool live = _thisPcActive;
            int candidatesClearingThreshold = UpdateLiveCandidatesLocked(message, rssi, senderTag, at);
            var input = new OwnershipInput(
                new ProximityParse(ProximityParseStatus.Ok, message, null, null, 1, Array.Empty<byte>()),
                rssi, _claim, table, live, candidatesClearingThreshold, at);
            OwnershipResult result = OwnershipRule.Evaluate(input);

            switch (result.Verdict)
            {
                case OwnershipVerdict.Owned:
                    Interlocked.Increment(ref _owned);
                    break;
                case OwnershipVerdict.OwnedByLiveConnection:
                    Interlocked.Increment(ref _ownedByLiveConnection);
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
                case OwnershipVerdict.AmbiguousCandidates:
                    Interlocked.Increment(ref _ambiguousCandidates);
                    return;
                default:
                    return;
            }

            if (result.UpdatedLast is OwnedBattery updated && _claim is not null)
            {
                _claim = _claim with { Last = updated };
                _claimStore.Save(_claim);
            }

            DecodedReading reading = ProximityDecoder.Decode(message, table, at);
            caseOpenedEdge = ApplyDecodedReadingLocked(reading, table, at);
        }

        if (caseOpenedEdge)
        {
            _uiPost(() => CaseOpened?.Invoke(this, new CaseOpenedEventArgs(caseOpenedAt)));
        }
    }

    // Must be called holding _gate. Prunes sender tags last seen outside WidgetTiming.LiveCandidateWindow,
    // then, when this message matches the current claim's model and colour and clears its threshold, records
    // its sender tag as a candidate. Returns the resulting distinct-candidate count.
    private int UpdateLiveCandidatesLocked(ProximityMessage message, sbyte rssi, uint senderTag, DateTimeOffset at)
    {
        List<uint>? stale = null;
        foreach (KeyValuePair<uint, DateTimeOffset> entry in _liveCandidates)
        {
            if (at - entry.Value > WidgetTiming.LiveCandidateWindow)
            {
                (stale ??= new List<uint>()).Add(entry.Key);
            }
        }

        if (stale is not null)
        {
            foreach (uint key in stale)
            {
                _liveCandidates.Remove(key);
            }
        }

        if (_claim is WidgetClaim claim &&
            message.ModelHigh == claim.ModelHigh && message.ModelLow == claim.ModelLow && message.Colour == claim.Colour &&
            rssi >= claim.SignalThresholdDbm)
        {
            _liveCandidates[senderTag] = at;
        }

        return _liveCandidates.Count;
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
            caseOpenedEdge = _lastLidCounter is null || _lastLidCounter.Value != counter;
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

    private void OnRetryDue()
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
            snapshot = BuildSnapshotLocked(_timeProvider.GetUtcNow());
            changed = !Equals(snapshot, _lastPublished);
            _lastPublished = snapshot;
        }

        if (changed)
        {
            _uiPost(() => Changed?.Invoke(this, EventArgs.Empty));
        }
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

        return new WidgetSnapshot(
            where, _left, _right, _case, batteryReadAt, earReadAt, lidOpen,
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
        var shapes = new (byte? Prefix, int Length, long Count)[_unknownForms.Count];
        int i = 0;
        foreach (KeyValuePair<(byte? Prefix, int Length), long> entry in _unknownForms)
        {
            shapes[i++] = (entry.Key.Prefix, entry.Key.Length, entry.Value);
        }

        return new WidgetCounters(
            Interlocked.Read(ref _allAdvertisements),
            Interlocked.Read(ref _appleSections),
            Interlocked.Read(ref _otherCompanySections),
            Interlocked.Read(ref _proximityItems),
            Interlocked.Read(ref _okForm),
            Interlocked.Read(ref _truncated),
            Interlocked.Read(ref _unknownForm),
            Interlocked.Read(ref _owned),
            Interlocked.Read(ref _ownedByLiveConnection),
            Interlocked.Read(ref _noClaim),
            Interlocked.Read(ref _modelOrColourMismatch),
            Interlocked.Read(ref _signalBelowThreshold),
            Interlocked.Read(ref _batteryUnreadable),
            Interlocked.Read(ref _batteryInconsistent),
            Interlocked.Read(ref _ambiguousCandidates),
            shapes);
    }
}
