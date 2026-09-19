using Earshot.Contracts;

namespace Earshot.Streaming;

// The state machine behind Play from a phone: which paired devices can send audio to this PC, which one the
// owner chose, whether it is playing, and the one sentence that says so. It draws nothing and owns no thread.
//
// Order with Windows, always: EnableAsync (TryCreateFromId, then StartAsync) and only then OpenAsync. "Before
// opening a connection with a device, the connection must be enabled."
// https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback
//
// One device at a time. Whether Windows can have two open at once is not documented, so a second choice
// releases the first before it enables anything.
//
// Nothing stays enabled that the owner cannot see and stop. An enable whose open then fails is released at
// once, so the only connection left enabled is the one in use, which the menu shows with its Stop item. That
// one stays enabled when the phone stops playing, so the phone can start again by itself; "the underlying
// transport is deactivated when all references are released", which is what StopPlaying and ReleaseAll do.
//
// Threading. LinkChanged arrives on whatever thread Windows calls back on, and the host may call from a pool
// thread, so every field below the lock is read and written inside that one lock, and the lock is never held
// across a call to the platform, to the busy gate, to the exclusion rule or to a Changed handler.
// StartPlayingAsync holds a semaphore for its whole body so two clicks cannot interleave. Nothing here blocks:
// every wait is an await with ConfigureAwait(false).
//
// No public member throws for anything Windows did. ArgumentNullException for a null argument is the only one.
internal sealed class StreamingCoordinator : IDisposable
{
    private const string ListStep = "streaming-list";
    private const string StartStep = "streaming-start";
    private const string StopStep = "streaming-stop";
    private const string ReleaseAllStep = "streaming-release-all";

    private readonly IStreamingPlatform _platform;
    private readonly IBusyGate _busyGate;
    private readonly StreamingSettings _settings;
    private readonly Func<StreamingDevice, bool> _isExcluded;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _state = new();

    // Everything from here to the constructor is guarded by _state.
    private readonly List<string> _enabled = new();          // ids handed to EnableAsync and not yet released, in order
    private readonly HashSet<string> _excludedIds = new(StringComparer.Ordinal);
    private List<StreamingDevice> _devices = [];             // the last good read, the managed device removed
    private StreamingDiscoveryStatus _lastDiscovery = StreamingDiscoveryStatus.NotStarted;
    private int _refreshing;
    private string? _activeId;
    private string _activeName = "";
    private StreamingLinkState _activeLink = StreamingLinkState.Unknown;
    private string? _pendingId;                               // the id a start in flight is enabling or opening
    private StreamingLinkState _pendingLink = StreamingLinkState.Unknown;
    private string _lastDeviceKey;
    private string _statusLine = "";
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
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(busyGate);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(isExcluded);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _platform = platform;
        _busyGate = busyGate;
        _settings = settings.Clamped(out IReadOnlyList<StepOutcome> notes);
        SettingsNotes = notes;
        _isExcluded = isExcluded;
        _time = timeProvider;
        _lastDeviceKey = _settings.LastDeviceKey;

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

    // The status line while a device is in use, and "" otherwise: what the tray adds to its tooltip. At rest,
    // with nothing in use, the tooltip is exactly what it was before this feature existed.
    public string TooltipLine
    {
        get
        {
            lock (_state)
            {
                return _activeId is null ? "" : _statusLine;
            }
        }
    }

    // Raised after Menu or StatusLine changes, on whatever thread caused it: a pool thread, or the thread
    // Windows reports a link change on. This class never touches a control and marshals nothing; the host gets
    // itself onto its UI thread (TrayContext posts through ServiceRegistry.UiPost).
    public event EventHandler? Changed;

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
                        string goneName = _activeName;
                        _activeId = null;
                        _activeName = "";
                        _activeLink = StreamingLinkState.Unknown;
                        _enabled.Remove(active);
                        _statusLine = StreamingCopy.For(StreamingCopy.DisconnectedFormat, goneName);
                    }

                    _menu = BuildMenuLocked();
                }
            }
        }

        if (goneId is not null)
        {
            SafeRelease(goneId);
        }

        RaiseChanged();
        return result;
    }

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
            ReleaseIfStillEnabled(deviceId);
            return new StreamingOpenOutcome(StreamingOpenStatus.CallFailed, deviceId, Failure(StartStep, ex));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // Stops accepting audio from the device in use. Never throws.
    public StreamingReleaseOutcome StopPlaying()
    {
        string id;
        string name;
        lock (_state)
        {
            if (_activeId is null)
            {
                return new StreamingReleaseOutcome(false, "", StepOutcomes.NotAttempted(StopStep, StreamingDetail.NothingOpen));
            }

            id = _activeId;
            name = _activeName;
            _activeId = null;
            _activeName = "";
            _activeLink = StreamingLinkState.Unknown;
            _enabled.Remove(id);
            _menu = BuildMenuLocked();
        }

        StreamingReleaseOutcome outcome = SafeRelease(id);
        lock (_state)
        {
            _statusLine = StreamingCopy.For(StreamingCopy.NoLongerAcceptingFormat, name);
            _menu = BuildMenuLocked();
        }

        RaiseChanged();
        return outcome;
    }

    // Releases every connection this coordinator enabled and has not released yet, in the order they were
    // enabled, and returns the outcome for the last one. The host calls it on every way out. It is final: a
    // start still in flight releases what it enabled when it comes back, and nothing starts afterwards.
    public StreamingReleaseOutcome ReleaseAll()
    {
        string[] ids;
        lock (_state)
        {
            _closed = true;
            ids = _enabled.ToArray();
            _enabled.Clear();
            _activeId = null;
            _activeName = "";
            _activeLink = StreamingLinkState.Unknown;
            _menu = BuildMenuLocked();
        }

        var last = new StreamingReleaseOutcome(false, "", StepOutcomes.NotAttempted(ReleaseAllStep, StreamingDetail.NothingEnabled));
        foreach (string id in ids)
        {
            last = SafeRelease(id);
        }

        RaiseChanged();
        return last;
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        ReleaseAll();
        _platform.LinkChanged -= OnLinkChanged;

        // Disposed only when no start holds it: one still in flight releases it on its way out, and must find
        // it there. A semaphore whose wait handle was never asked for holds nothing the collector cannot free.
        if (_oneAtATime.Wait(0))
        {
            _oneAtATime.Dispose();
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
        string? replaced;
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

            // The device in use, chosen again while its link is up: Windows is not asked to open what is open.
            // Once the phone has closed the link, the same click opens it again.
            if (string.Equals(_activeId, deviceId, StringComparison.Ordinal) && _activeLink != StreamingLinkState.Closed)
            {
                return new StreamingOpenOutcome(StreamingOpenStatus.Open, deviceId, StepOutcomes.FromHResult(StartStep, 0, StreamingDetail.AlreadyOpen));
            }

            replaced = _activeId is not null && !string.Equals(_activeId, deviceId, StringComparison.Ordinal) ? _activeId : null;
        }

        if (replaced is not null)
        {
            StopPlaying();
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

        StreamingEnableOutcome enable;
        try
        {
            enable = await _platform.EnableAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseIfStillEnabled(deviceId);
            return NotStarted(deviceId, StreamingDetail.Cancelled);
        }

        if (enable.Status != StreamingEnableStatus.Enabled)
        {
            ReleaseIfStillEnabled(deviceId);
            SetStatus(StreamingCopy.For(
                enable.Status == StreamingEnableStatus.NotStreamCapable ? StreamingCopy.CannotSendAudioFormat : StreamingCopy.CouldNotGetReadyFormat,
                name));
            return new StreamingOpenOutcome(StreamingOpenStatus.NotEnabled, deviceId, enable.Step);
        }

        if (!StillEnabled(deviceId))
        {
            // ReleaseAll ran while Windows was enabling it, before there was anything to release.
            SafeRelease(deviceId);
            return NotStarted(deviceId, StreamingDetail.ReleasedMeanwhile);
        }

        StreamingOpenOutcome open = await OpenWithinLimitAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (open.Status != StreamingOpenStatus.Open)
        {
            // Nothing the owner cannot see stays enabled.
            ReleaseIfStillEnabled(deviceId);
            if (open.Status != StreamingOpenStatus.NotStarted)
            {
                SetStatus(StreamingCopy.For(FailureFormat(open), name));
            }

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
                _lastDeviceKey = StreamingLog.Key(deviceId);
                _statusLine = StreamingCopy.For(
                    _activeLink == StreamingLinkState.Closed ? StreamingCopy.DisconnectedFormat : StreamingCopy.WaitingFormat,
                    name);
                _menu = BuildMenuLocked();
            }
        }

        if (!took)
        {
            SafeRelease(deviceId);
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

    // Releases deviceId unless ReleaseAll or StopPlaying already has.
    private void ReleaseIfStillEnabled(string deviceId)
    {
        bool mine;
        lock (_state)
        {
            mine = _enabled.Remove(deviceId);
            if (mine && string.Equals(_activeId, deviceId, StringComparison.Ordinal))
            {
                _activeId = null;
                _activeName = "";
                _activeLink = StreamingLinkState.Unknown;
                _menu = BuildMenuLocked();
            }
        }

        if (mine)
        {
            SafeRelease(deviceId);
        }
    }

    private StreamingReleaseOutcome SafeRelease(string deviceId)
    {
        try
        {
            return _platform.Release(deviceId);
        }
        catch (Exception ex)
        {
            // The platform contract is not to throw from Release. One that does is still recorded.
            return new StreamingReleaseOutcome(false, deviceId, Failure("streaming-release", ex));
        }
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

    // The raw HRESULT and the exception type. Never the message: text from Windows can carry a device path.
    internal static StepOutcome Failure(string step, Exception ex) =>
        new(step, Ok: false, ex.HResult, NativeCodes.Name(ex.HResult), ex.GetType().Name);

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
            foreach (StreamingDevice device in _devices.OrderBy(d => StreamingLog.Key(d.DeviceId) == _lastDeviceKey ? 0 : 1))
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

        if (_activeId is not null)
        {
            items.Add(new StreamingMenuItem(StreamingLabels.Stop(_activeName), Enabled: true, Checked: false, _activeId, StreamingMenuCommand.Stop));
        }

        items.Add(new StreamingMenuItem(StreamingLabels.Refresh, Enabled: !_closed, Checked: false, DeviceId: null, StreamingMenuCommand.Refresh));
        return new StreamingMenuModel(StreamingLabels.Parent, ParentEnabled: true, items);
    }
}
