using Earshot.Contracts;

namespace Earshot.Streaming;

// The state machine behind Play from a phone: which paired devices can send audio to this PC, which one the
// owner chose, whether its link is up, and the one sentence that says so. It draws nothing and owns no thread.
//
// Order with Windows, always: EnableAsync (TryCreateFromId, then StartAsync) and only then OpenAsync. "Before
// opening a connection with a device, the connection must be enabled."
// https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback
//
// One device at a time. Whether Windows can have two open at once is not documented, so a second choice
// releases the first before it enables anything, and is refused if Windows does not confirm that release.
//
// Nothing stays enabled that the menu does not show. An enable whose open then fails is released at once, so
// the only connection left enabled is the one in use, which the menu shows with its Stop item. That one stays
// enabled when the phone lets go, so the phone can come back by itself; "the underlying transport is
// deactivated when all references are released", which is what StopPlaying and ReleaseAll do.
//
// Every release goes through one method, Release, and that is the whole point of it: the outcome of every one
// is written to the log with its raw code, none is dropped, and when Windows does not confirm a release the
// device stays in the menu with its Stop item, the owner is told once that it may still be connected, and no
// line claims this PC stopped accepting audio. The platform releases nothing by itself, so there is no second
// place for an outcome to go missing.
//
// Threading. LinkChanged arrives on whatever thread Windows calls back on, and the host may call from a pool
// thread, so every field below the lock is read and written inside that one lock, and the lock is never held
// across a call to the platform, to the busy gate, to the exclusion rule, to the log or to a Changed handler.
// StartPlayingAsync holds a semaphore for its whole body so two clicks cannot interleave. Nothing here blocks:
// every wait is an await with ConfigureAwait(false).
//
// No public member throws for anything Windows did. ArgumentNullException for a null argument is the only one.
internal sealed class StreamingCoordinator : IDisposable
{
    private const string ListStep = "streaming-list";
    private const string StartStep = "streaming-start";
    private const string StopStep = "streaming-stop";
    private const string ReleaseStep = "streaming-release";

    private readonly IStreamingPlatform _platform;
    private readonly IBusyGate _busyGate;
    private readonly StreamingSettings _settings;
    private readonly Func<StreamingDevice, bool> _isExcluded;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _state = new();

    // Everything from here to the constructor is guarded by _state.
    private readonly List<string> _enabled = new();          // ids handed to EnableAsync whose release Windows has not confirmed, in order
    private readonly List<(string Id, string Name)> _unreleased = new();   // of those, the ones a release has failed for
    private readonly HashSet<string> _excludedIds = new(StringComparer.Ordinal);
    private List<StreamingDevice> _devices = [];             // the last good read, the managed device removed
    private StreamingDiscoveryStatus _lastDiscovery = StreamingDiscoveryStatus.NotStarted;
    private int _refreshing;
    private string? _activeId;
    private string _activeName = "";
    private StreamingLinkState _activeLink = StreamingLinkState.Unknown;
    private string? _pendingId;                               // the id a start in flight is enabling or opening
    private StreamingLinkState _pendingLink = StreamingLinkState.Unknown;
    private string? _lastDeviceId;                            // for this run only: nothing about a device is saved
    private string _statusLine = "";
    private string? _releaseFailureNotice;
    private StreamingMenuModel _menu;
    private bool _closed;
    private bool _disposed;

    // isExcluded: true for the device Earshot manages, which is never shown, never enabled and never opened.
    // The tray wires it to the pinned container; it is asked about what Windows reported, never about an id.
    public StreamingCoordinator(
        IStreamingPlatform platform,
        IBusyGate busyGate,
        StreamingSettings settings,
        Func<StreamingDevice, bool> isExcluded,
        TimeProvider timeProvider,
        ILog log)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(busyGate);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(isExcluded);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);

        _platform = platform;
        _busyGate = busyGate;
        _settings = settings.Clamped(out IReadOnlyList<StepOutcome> notes);
        SettingsNotes = notes;
        _isExcluded = isExcluded;
        _time = timeProvider;
        _log = log;

        // Asked once and kept: the menu is painted often, and the answer cannot change while Earshot runs.
        StreamingSupportCheck check = platform.CheckSupport();
        Support = check.Support;
        SupportStep = check.Step;

        _statusLine = StreamingCopy.ForSupport(Support);
        _menu = BuildMenuLocked();
        _platform.LinkChanged += OnLinkChanged;
    }

    public StreamingSupport Support { get; }

    // How the support check went, with the HRESULT when the type did not answer.
    public StepOutcome SupportStep { get; }

    // One note per limit that was outside its range and replaced by the default. Empty when none was.
    public IReadOnlyList<StepOutcome> SettingsNotes { get; }

    // Never null, rebuilt after every step.
    public StreamingMenuModel Menu
    {
        get
        {
            lock (_state)
            {
                return _menu;
            }
        }
    }

    // The last thing worth telling the owner.
    public string StatusLine
    {
        get
        {
            lock (_state)
            {
                return _statusLine;
            }
        }
    }

    // What the tray adds to its tooltip: that a device may still be connected, for as long as Windows has not
    // confirmed letting go of it; otherwise the status line while a device is in use; otherwise "". At rest, with
    // nothing in use and nothing unconfirmed, the tooltip is exactly what it was before this feature existed.
    public string TooltipLine
    {
        get
        {
            lock (_state)
            {
                return _unreleased.Count > 0 ? StreamingCopy.For(StreamingCopy.MayStillBeConnectedFormat, _unreleased[0].Name)
                     : _activeId is null ? ""
                     : _statusLine;
            }
        }
    }

    // Raised after Menu or StatusLine changes, on whatever thread caused it: a pool thread, or the thread
    // Windows reports a link change on. This class never touches a control and marshals nothing; the host gets
    // itself onto its UI thread (TrayContext posts through ServiceRegistry.UiPost).
    public event EventHandler? Changed;

    // The sentence that says a device may still be connected, once for each release Windows did not confirm, and
    // null otherwise. The host asks at the end of everything it starts here and shows what it gets on one card.
    public string? TakeReleaseFailureNotice()
    {
        lock (_state)
        {
            string? notice = _releaseFailureNotice;
            _releaseFailureNotice = null;
            return notice;
        }
    }

    public async Task<StreamingDiscovery> RefreshAsync(CancellationToken cancellationToken)
    {
        if (Support != StreamingSupport.Supported)
        {
            return new StreamingDiscovery(StreamingDiscoveryStatus.Unsupported, [], StepOutcomes.NotAttempted(ListStep, StreamingDetail.Support));
        }

        lock (_state)
        {
            if (_closed)
            {
                return new StreamingDiscovery(StreamingDiscoveryStatus.NotStarted, [], StepOutcomes.NotAttempted(ListStep, StreamingDetail.Closed));
            }

            _refreshing++;
            _menu = BuildMenuLocked();
        }

        // From here to the finally nothing may leave without the count above being put back: a read left counted
        // would keep the menu saying it is looking for devices for good. That covers a platform that breaks its word
        // and throws, a host rule that throws, and a Changed handler that throws.
        var result = new StreamingDiscovery(StreamingDiscoveryStatus.Failed, [], StepOutcomes.NotAttempted(ListStep, StreamingDetail.Cancelled));
        var kept = new List<StreamingDevice>();
        var excluded = new List<string>();
        string? goneId = null;
        string goneName = "";
        try
        {
            RaiseChanged();
            StreamingDiscovery read = await ReadWithinLimitAsync(cancellationToken).ConfigureAwait(false);

            // Outside the lock: the rule belongs to the host and may read its settings.
            if (read.Status == StreamingDiscoveryStatus.Ok)
            {
                foreach (StreamingDevice device in read.Devices)
                {
                    if (_isExcluded(device))
                    {
                        excluded.Add(device.DeviceId);
                    }
                    else
                    {
                        kept.Add(device);
                    }
                }
            }

            result = new StreamingDiscovery(read.Status, kept, read.Step);
        }
        catch (Exception ex)
        {
            // If the rule could not say which device is the managed one, none is offered.
            kept = [];
            excluded = [];
            result = new StreamingDiscovery(StreamingDiscoveryStatus.Failed, [], Failure(ListStep, ex));
        }
        finally
        {
            lock (_state)
            {
                _refreshing--;
                if (!_closed)
                {
                    _lastDiscovery = result.Status;
                    _devices = kept;
                    _excludedIds.Clear();
                    _excludedIds.UnionWith(excluded);

                    // "You should handle the case where a device is removed while a connection is enabled or open":
                    // a device in use that a good read no longer holds has been unpaired, or has become the device
                    // Earshot manages, so its connection is let go. Only a good read decides this, never a failed one.
                    // https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback
                    if (result.Status == StreamingDiscoveryStatus.Ok && _activeId is { } active &&
                        !kept.Any(d => string.Equals(d.DeviceId, active, StringComparison.Ordinal)))
                    {
                        goneId = active;
                        goneName = _activeName;
                        ClearActiveLocked();
                        _statusLine = StreamingCopy.For(StreamingCopy.DisconnectedFormat, goneName);
                    }

                    _menu = BuildMenuLocked();
                }
            }
        }

        if (goneId is not null)
        {
            Release(goneId, goneName, "the list no longer holds it");
        }

        RaiseChanged();
        return result;
    }

    public async Task<StreamingOpenOutcome> StartPlayingAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        try
        {
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NotStarted(deviceId, StreamingDetail.Cancelled);
        }
        catch (ObjectDisposedException)
        {
            return NotStarted(deviceId, StreamingDetail.Closed);
        }

        try
        {
            return await StartPlayingCoreAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing below is meant to throw. If something does, whatever this call enabled is not left behind.
            LetGo(deviceId, NameOf(deviceId), "the start threw " + ex.GetType().Name);
            return new StreamingOpenOutcome(StreamingOpenStatus.CallFailed, deviceId, Failure(StartStep, ex));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // Stops accepting audio from one device: the one named, or the one in use, or failing that one Windows has not
    // yet confirmed letting go of, which is how the owner tries that again from the same menu item. Never throws.
    // When Windows does not confirm, nothing here says this PC stopped accepting audio: the device stays in the menu.
    public StreamingReleaseOutcome StopPlaying(string? deviceId = null)
    {
        string id;
        string name;
        lock (_state)
        {
            string? wanted = deviceId ?? _activeId ?? (_unreleased.Count > 0 ? _unreleased[0].Id : null);
            if (wanted is null || !_enabled.Contains(wanted))
            {
                return new StreamingReleaseOutcome(false, deviceId ?? "", StepOutcomes.NotAttempted(StopStep, StreamingDetail.NothingOpen));
            }

            id = wanted;
            name = NameOfLocked(id);
            if (string.Equals(_activeId, id, StringComparison.Ordinal))
            {
                ClearActiveLocked();
            }

            _menu = BuildMenuLocked();
        }

        StreamingReleaseOutcome outcome = Release(id, name, "stop");
        if (!outcome.Failed)
        {
            lock (_state)
            {
                _statusLine = StreamingCopy.For(StreamingCopy.NoLongerAcceptingFormat, name);
                _menu = BuildMenuLocked();
            }
        }

        RaiseChanged();
        return outcome;
    }

    // Releases every connection this coordinator enabled and Windows has not confirmed letting go of, in the order
    // they were enabled, and returns the outcome of every one. The host calls it on every way out. It is final: a
    // start still in flight releases what it enabled when it comes back, and nothing starts afterwards.
    public IReadOnlyList<StreamingReleaseOutcome> ReleaseAll()
    {
        (string Id, string Name)[] held;
        lock (_state)
        {
            _closed = true;
            held = _enabled.Select(id => (id, NameOfLocked(id))).ToArray();
            ClearActiveLocked();
            _menu = BuildMenuLocked();
        }

        var outcomes = new List<StreamingReleaseOutcome>(held.Length);
        foreach ((string id, string name) in held)
        {
            outcomes.Add(Release(id, name, "everything is being let go"));
        }

        RaiseChanged();
        return outcomes;
    }

    public void Dispose()
    {
        bool alreadyLetGo;
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            alreadyLetGo = _closed;
        }

        // Not a second attempt at what ReleaseAll has already tried: its outcomes are recorded, and the references
        // end with the coordinator.
        if (!alreadyLetGo)
        {
            ReleaseAll();
        }

        _platform.LinkChanged -= OnLinkChanged;

        // Disposed only when no start holds it: one still in flight releases it on its way out, and must find
        // it there. A semaphore whose wait handle was never asked for holds nothing the collector cannot free.
        if (_oneAtATime.Wait(0))
        {
            _oneAtATime.Dispose();
        }
    }

    // The raw HRESULT and the exception type. Never the message: text from Windows can carry a device path.
    internal static StepOutcome Failure(string step, Exception ex) =>
        new(step, Ok: false, ex.HResult, NativeCodes.Name(ex.HResult), ex.GetType().Name);

    private async Task<StreamingDiscovery> ReadWithinLimitAsync(CancellationToken cancellationToken)
    {
        // The one API that takes a TimeProvider: CancelAfter has no such overload.
        // https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.-ctor
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.DiscoveryTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            return await _platform.ListStreamCapableDevicesAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            string why = cancellationToken.IsCancellationRequested ? StreamingDetail.Cancelled : StreamingDetail.LocalTimeout;
            return new StreamingDiscovery(StreamingDiscoveryStatus.Failed, [], StepOutcomes.NotAttempted(ListStep, why));
        }
    }

    private async Task<StreamingOpenOutcome> StartPlayingCoreAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (Support != StreamingSupport.Supported)
        {
            SetStatus(StreamingCopy.ForSupport(Support));
            return new StreamingOpenOutcome(StreamingOpenStatus.Unsupported, deviceId, StepOutcomes.NotAttempted(StartStep, StreamingDetail.Support));
        }

        if (_busyGate.IsBusy)
        {
            string reason = _busyGate.BusyReason;
            SetStatus(StreamingCopy.BusyJustNow);
            return NotStarted(deviceId, reason);
        }

        StreamingDevice? device;
        lock (_state)
        {
            if (_closed)
            {
                return NotStarted(deviceId, StreamingDetail.Closed);
            }

            // Decided from the last read of the list: a device Earshot manages, or one that list never held,
            // is not something the owner could have clicked, so there is no sentence for either.
            if (_excludedIds.Contains(deviceId))
            {
                return NotStarted(deviceId, StreamingDetail.Excluded);
            }

            device = _devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
            if (device is null)
            {
                return NotStarted(deviceId, StreamingDetail.UnknownDevice);
            }
        }

        // The rule is asked again now, not only when the list was read: a device pinned as the one Earshot manages
        // since then is still on the menu, because a pin is no reason to read the list again. Outside the lock, as
        // the rule belongs to the host. If the rule cannot say, nothing is started.
        bool managed;
        try
        {
            managed = _isExcluded(device);
        }
        catch (Exception ex)
        {
            return new StreamingOpenOutcome(StreamingOpenStatus.NotStarted, deviceId, Failure(StartStep, ex));
        }

        if (managed)
        {
            return RefuseTheManagedDevice(device);
        }

        string? replaced;
        (string Id, string Name)[] unconfirmed;
        lock (_state)
        {
            // The device in use, chosen again while its link is up: Windows is not asked to open what is open.
            // Once the phone has closed the link, the same click opens it again.
            if (string.Equals(_activeId, deviceId, StringComparison.Ordinal) && _activeLink != StreamingLinkState.Closed)
            {
                return new StreamingOpenOutcome(StreamingOpenStatus.Open, deviceId, StepOutcomes.FromHResult(StartStep, 0, StreamingDetail.AlreadyOpen));
            }

            replaced = _activeId is not null && !string.Equals(_activeId, deviceId, StringComparison.Ordinal) ? _activeId : null;
            unconfirmed = _unreleased.ToArray();
        }

        // One device at a time only holds if the last one was really let go. Anything Windows has not confirmed
        // letting go of is tried again first, then the device in use; if either still fails, nothing new is enabled.
        foreach ((string id, string name) in unconfirmed)
        {
            if (Release(id, name, "before another start").Failed)
            {
                RaiseChanged();
                return NotStarted(deviceId, StreamingDetail.ReleaseFailed);
            }
        }

        if (replaced is not null && StopPlaying(replaced).Failed)
        {
            return NotStarted(deviceId, StreamingDetail.ReleaseFailed);
        }

        try
        {
            return await EnableThenOpenAsync(device, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_state)
            {
                if (string.Equals(_pendingId, deviceId, StringComparison.Ordinal))
                {
                    _pendingId = null;
                    _pendingLink = StreamingLinkState.Unknown;
                }
            }
        }
    }

    // The device has become the one Earshot manages since the list was read. It comes off the menu at once, and if
    // it was the one in use it is let go of, as a read of the list would have done.
    private StreamingOpenOutcome RefuseTheManagedDevice(StreamingDevice device)
    {
        bool wasInUse;
        lock (_state)
        {
            _excludedIds.Add(device.DeviceId);
            _devices = _devices.Where(d => !string.Equals(d.DeviceId, device.DeviceId, StringComparison.Ordinal)).ToList();
            wasInUse = string.Equals(_activeId, device.DeviceId, StringComparison.Ordinal);
            if (wasInUse)
            {
                ClearActiveLocked();
                _statusLine = StreamingCopy.For(StreamingCopy.DisconnectedFormat, device.DisplayName);
            }

            _menu = BuildMenuLocked();
        }

        if (wasInUse)
        {
            Release(device.DeviceId, device.DisplayName, "it is the device Earshot manages");
        }

        RaiseChanged();
        return NotStarted(device.DeviceId, StreamingDetail.Excluded);
    }

    private async Task<StreamingOpenOutcome> EnableThenOpenAsync(StreamingDevice device, CancellationToken cancellationToken)
    {
        string deviceId = device.DeviceId;
        string name = device.DisplayName;

        // Written down before Windows is asked, so a ReleaseAll that lands while EnableAsync is still running
        // knows about this id, and this method sees on its return that it was released meanwhile.
        lock (_state)
        {
            if (_closed)
            {
                return NotStarted(deviceId, StreamingDetail.Closed);
            }

            if (!_enabled.Contains(deviceId))
            {
                _enabled.Add(deviceId);
            }

            // Windows may report the link before OpenAsync returns, so what it says meanwhile is kept.
            _pendingId = deviceId;
            _pendingLink = StreamingLinkState.Unknown;
        }

        // A failed or cancelled enable may have left a connection created, and the platform lets go of nothing by
        // itself, so every way out of here that is not a success lets go of it, through the one method that records it.
        StreamingEnableOutcome enable;
        try
        {
            enable = await _platform.EnableAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LetGo(deviceId, name, "the start was cancelled");
            return NotStarted(deviceId, StreamingDetail.Cancelled);
        }

        if (enable.Status != StreamingEnableStatus.Enabled)
        {
            // The sentence first, the release second: if Windows does not confirm the release, that is what the
            // owner most needs to know, and it takes the line.
            SetStatus(StreamingCopy.For(
                enable.Status == StreamingEnableStatus.NotStreamCapable ? StreamingCopy.CannotSendAudioFormat : StreamingCopy.CouldNotGetReadyFormat,
                name));
            LetGo(deviceId, name, "it could not be enabled");
            return new StreamingOpenOutcome(StreamingOpenStatus.NotEnabled, deviceId, enable.Step);
        }

        if (!StillEnabled(deviceId))
        {
            // ReleaseAll ran while Windows was enabling it, before there was anything to release.
            LetGo(deviceId, name, "everything was let go while it was being enabled");
            return NotStarted(deviceId, StreamingDetail.ReleasedMeanwhile);
        }

        StreamingOpenOutcome open = await OpenWithinLimitAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (open.Status != StreamingOpenStatus.Open)
        {
            if (open.Status != StreamingOpenStatus.NotStarted)
            {
                SetStatus(StreamingCopy.For(FailureFormat(open), name));
            }

            // Nothing the menu does not show stays enabled.
            LetGo(deviceId, name, "the open did not succeed");
            return open;
        }

        bool took;
        lock (_state)
        {
            took = !_closed && _enabled.Contains(deviceId);
            if (took)
            {
                _activeId = deviceId;
                _activeName = name;

                // Opened while OpenAsync ran is what a successful open looks like, so it says nothing more than
                // the open did. Closed meanwhile means the phone has already let go, and that is what is shown.
                _activeLink = _pendingLink == StreamingLinkState.Closed ? StreamingLinkState.Closed : StreamingLinkState.Unknown;
                _lastDeviceId = deviceId;
                _statusLine = StreamingCopy.For(
                    _activeLink == StreamingLinkState.Closed ? StreamingCopy.DisconnectedFormat : StreamingCopy.WaitingFormat,
                    name);
                _menu = BuildMenuLocked();
            }
        }

        if (!took)
        {
            LetGo(deviceId, name, "everything was let go while it was being opened");
            return NotStarted(deviceId, StreamingDetail.ReleasedMeanwhile);
        }

        RaiseChanged();
        return open;
    }

    private async Task<StreamingOpenOutcome> OpenWithinLimitAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.OpenTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            return await _platform.OpenAsync(deviceId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's own cancellation is not a timeout, and says nothing to the owner: Earshot is closing.
            return cancellationToken.IsCancellationRequested
                ? NotStarted(deviceId, StreamingDetail.Cancelled)
                : new StreamingOpenOutcome(StreamingOpenStatus.TimedOut, deviceId, StepOutcomes.NotAttempted(StartStep, StreamingDetail.LocalTimeout));
        }
    }

    // The two timeouts share a sentence on purpose: the owner can do nothing different about them. They are
    // told apart in the outcome, where only Earshot's own carries the "local timeout" detail. An unknown
    // failure with no code from Windows has nothing to look up in the log, so it reads as a plain failure.
    private static string FailureFormat(StreamingOpenOutcome open) => open.Status switch
    {
        StreamingOpenStatus.TimedOut => StreamingCopy.DidNotAnswerFormat,
        StreamingOpenStatus.DeniedBySystem => StreamingCopy.RefusedFormat,
        StreamingOpenStatus.UnknownFailure when open.Step.Code != 0 && open.Step.Code != NativeCodes.NotAttempted => StreamingCopy.CouldNotConnectSeeLogFormat,
        _ => StreamingCopy.CouldNotConnectFormat,
    };

    private void OnLinkChanged(object? sender, StreamingLinkChanged e)
    {
        bool changed = false;
        lock (_state)
        {
            if (_closed || e.State == StreamingLinkState.Unknown)
            {
                return;
            }

            if (string.Equals(_pendingId, e.DeviceId, StringComparison.Ordinal))
            {
                _pendingLink = e.State;
            }

            // Only the device in use speaks here. A late report for one already released changes nothing.
            if (_activeId is not null && string.Equals(_activeId, e.DeviceId, StringComparison.Ordinal))
            {
                _activeLink = e.State;
                _statusLine = StreamingCopy.For(
                    e.State == StreamingLinkState.Opened ? StreamingCopy.ConnectedFormat : StreamingCopy.DisconnectedFormat,
                    _activeName);
                _menu = BuildMenuLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private bool StillEnabled(string deviceId)
    {
        lock (_state)
        {
            return !_closed && _enabled.Contains(deviceId);
        }
    }

    // Takes a device out of use, if it was in use, and releases it. Always releases: a second release of something
    // already let go is answered "nothing to release", which costs nothing, where a release skipped is a connection
    // left enabled with nobody knowing.
    private void LetGo(string deviceId, string name, string why)
    {
        lock (_state)
        {
            if (string.Equals(_activeId, deviceId, StringComparison.Ordinal))
            {
                ClearActiveLocked();
                _menu = BuildMenuLocked();
            }
        }

        Release(deviceId, name, why);
        RaiseChanged();
    }

    // The one place a connection is released, so the one place an outcome could be lost, and it loses none: every
    // outcome is logged with its raw code and the exception type, the device named by its key and never by its id.
    // When Windows does not confirm, the device is kept: still counted as enabled, so ReleaseAll tries it again, and
    // still in the menu, so the owner can. The status line and the notice say it may still be connected.
    private StreamingReleaseOutcome Release(string deviceId, string name, string why)
    {
        StreamingReleaseOutcome outcome;
        try
        {
            outcome = _platform.Release(deviceId);
        }
        catch (Exception ex)
        {
            // The platform contract is not to throw from Release. One that does is recorded like any other failure.
            outcome = new StreamingReleaseOutcome(false, deviceId, Failure(ReleaseStep, ex));
        }

        string key = StreamingLog.Key(deviceId);
        if (outcome.Failed)
        {
            _log.Warn("Play from a phone: device " + key + " was not released (" + why + ") and may still be connected to this PC. " + StreamingLog.Describe(outcome.Step));
        }
        else if (outcome.Released)
        {
            _log.Info("Play from a phone: released device " + key + " (" + why + "). " + StreamingLog.Describe(outcome.Step));
        }
        else
        {
            _log.Write(LogLevel.Debug, "Play from a phone: nothing to release for device " + key + " (" + why + "). " + StreamingLog.Describe(outcome.Step));
        }

        lock (_state)
        {
            if (outcome.Failed)
            {
                if (!_unreleased.Any(u => string.Equals(u.Id, deviceId, StringComparison.Ordinal)))
                {
                    _unreleased.Add((deviceId, name));
                }

                if (!_enabled.Contains(deviceId))
                {
                    _enabled.Add(deviceId);
                }

                _statusLine = StreamingCopy.For(StreamingCopy.MayStillBeConnectedFormat, name);
                _releaseFailureNotice = _statusLine;
            }
            else
            {
                _unreleased.RemoveAll(u => string.Equals(u.Id, deviceId, StringComparison.Ordinal));
                _enabled.Remove(deviceId);
            }

            _menu = BuildMenuLocked();
        }

        return outcome;
    }

    private void SetStatus(string line)
    {
        lock (_state)
        {
            _statusLine = line;
            _menu = BuildMenuLocked();
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static StreamingOpenOutcome NotStarted(string deviceId, string detail) =>
        new(StreamingOpenStatus.NotStarted, deviceId, StepOutcomes.NotAttempted(StartStep, detail));

    private string NameOf(string deviceId)
    {
        lock (_state)
        {
            return NameOfLocked(deviceId);
        }
    }

    // The name a device was last known by, for a sentence about it. "" when it was never known: the copy then says
    // "The device".
    private string NameOfLocked(string deviceId)
    {
        foreach ((string id, string name) in _unreleased)
        {
            if (string.Equals(id, deviceId, StringComparison.Ordinal))
            {
                return name;
            }
        }

        if (string.Equals(_activeId, deviceId, StringComparison.Ordinal))
        {
            return _activeName;
        }

        return _devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal))?.DisplayName ?? "";
    }

    private void ClearActiveLocked()
    {
        _activeId = null;
        _activeName = "";
        _activeLink = StreamingLinkState.Unknown;
    }

    private StreamingMenuModel BuildMenuLocked()
    {
        if (Support != StreamingSupport.Supported)
        {
            return new StreamingMenuModel(StreamingLabels.Parent, ParentEnabled: false, [StreamingMenuModel.Sentence(StreamingCopy.ForSupport(Support))]);
        }

        var items = new List<StreamingMenuItem>();
        bool haveList = _lastDiscovery == StreamingDiscoveryStatus.Ok;
        if (_refreshing > 0 && !haveList)
        {
            items.Add(StreamingMenuModel.Sentence(StreamingCopy.LookingForDevices));
        }
        else if (_lastDiscovery is StreamingDiscoveryStatus.Failed or StreamingDiscoveryStatus.Unsupported)
        {
            items.Add(StreamingMenuModel.Sentence(StreamingCopy.CouldNotReadList));
        }
        else if (haveList && _devices.Count == 0)
        {
            items.Add(StreamingMenuModel.Sentence(StreamingCopy.NoPairedDevice));
            items.Add(StreamingMenuModel.Sentence(StreamingCopy.PairInSettings));
        }
        else if (haveList)
        {
            // The device last played from comes first; the rest keep the order Windows gave.
            foreach (StreamingDevice device in _devices.OrderBy(d => string.Equals(d.DeviceId, _lastDeviceId, StringComparison.Ordinal) ? 0 : 1))
            {
                bool inUse = string.Equals(device.DeviceId, _activeId, StringComparison.Ordinal);
                items.Add(new StreamingMenuItem(
                    StreamingCopy.SafeName(device.DisplayName),
                    Enabled: !_closed,
                    Checked: inUse && _activeLink != StreamingLinkState.Closed,
                    device.DeviceId,
                    StreamingMenuCommand.Play));
            }
        }

        // A Stop item for the device in use, and one for every device Windows has not confirmed letting go of,
        // whether or not the list still holds it: that is what "nothing stays enabled that the menu does not show"
        // comes down to.
        if (_activeId is not null)
        {
            items.Add(new StreamingMenuItem(StreamingLabels.Stop(_activeName), Enabled: true, Checked: false, _activeId, StreamingMenuCommand.Stop));
        }

        foreach ((string id, string name) in _unreleased)
        {
            if (!string.Equals(id, _activeId, StringComparison.Ordinal))
            {
                items.Add(new StreamingMenuItem(StreamingLabels.Stop(name), Enabled: true, Checked: false, id, StreamingMenuCommand.Stop));
            }
        }

        items.Add(new StreamingMenuItem(StreamingLabels.Refresh, Enabled: !_closed, Checked: false, DeviceId: null, StreamingMenuCommand.Refresh));
        return new StreamingMenuModel(StreamingLabels.Parent, ParentEnabled: true, items);
    }
}
