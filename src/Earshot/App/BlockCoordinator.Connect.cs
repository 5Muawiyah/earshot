using System.Globalization;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Tray;

namespace Earshot.App;

// The connect: from every state the nodes can be in, inside one time limit, with one retry for a temporary failure, a plain
// reason when it fails, and exactly the allow it made put back.
//
//   1. Send the reconnect request. Confirmed: done (direct).
//   2. Every render endpoint NOTPRESENT (the nodes are blocked, or are flagged and away): allow first. The gate enables the
//      device node and what is in the tree, waits for what is away to be reported and enables that too. The coordinator waits
//      for a render endpoint, then sends the request (allow-first). The allow's nonce is kept: it names the status file that
//      lists what the allow changed, and a connect that does not reach ACTIVE has that undone, and nothing else.
//   3. A failure is classified (ConnectFailureClassifier). A temporary one is tried once more, when at least RetryRoom is left
//      of ConnectBudget. Anything else, or a second temporary one, ends the connect with the class's plain sentence.
//   4. Not confirmed with the A2DP filter turned down and protection on: the Hands-Free assisted way, as before.
// Everything below the limit is cut by one token, so a connect never runs past ConnectBudget; the clean-up that follows is not
// cancellable.
internal sealed partial class BlockCoordinator
{
    // The whole connect, from the click. A figure from the connect's requirements, not a measurement.
    public static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(20);

    // The least that must be left for the one retry to be worth starting.
    public static readonly TimeSpan RetryRoom = TimeSpan.FromSeconds(6);

    // How long a Connect in Windows keeps the idle rule off while the person pairs or connects in Windows' settings. A waiting
    // budget, not a measured or documented figure.
    public static readonly TimeSpan WindowsConnectWindowLength = TimeSpan.FromMinutes(2);

    // How long a link that the device node reports and no endpoint confirms is held back from the idle block before it is
    // blocked and reported. A waiting budget, not a measured figure.
    public static readonly TimeSpan UnconfirmedLinkCap = TimeSpan.FromMinutes(2);

    // How long a device node that reads connected while its endpoint is still not playing is given before the bit is taken as
    // stuck. A connect may raise the bit some seconds before A2DP reaches ACTIVE, so the two may disagree for a while on a good
    // link; that order is reasoned, not measured. A waiting budget, not a measured figure.
    public static readonly TimeSpan LinkSettle = TimeSpan.FromSeconds(8);

    // How long a link that formed with no one connecting it waits before it is blocked.
    public static readonly TimeSpan ForeignLinkDelay = TimeSpan.FromSeconds(2);

    public const string WaitingStatus = "Waiting for the AirPods";
    public const string TryingAgainStatus = "Trying again";
    public const string ConnectInWindowsMessage = "Connect the AirPods in Windows settings.";
    public const string ConnectInWindowsFailedToOpenMessage = "Could not open Windows Bluetooth settings.";
    public const string BluetoothSettingsOpenMessage = "Bluetooth settings is open. Connect your AirPods there.";
    public const string NoAirPodsPairedMessage = "No AirPods are paired with this PC.";

    private DateTimeOffset _windowsConnectUntil = DateTimeOffset.MinValue;
    private CancellationTokenSource? _windowTimer;

    // A link that formed with the owner at the PC (or by the owner's own connect) and has not dropped since.
    private bool _ownerLink;

    // When a link that reads connected with no endpoint to confirm it was first held back from the idle block.
    private DateTimeOffset? _unconfirmedSince;

    // True while the two minutes after Connect in Windows are running.
    public bool WindowsConnectOpen => _time.GetUtcNow() < _windowsConnectUntil;

    // What a connect knows as it goes, for the report and the history.
    private sealed class ConnectAttempt
    {
        public ConnectAttempt(TimeProvider time, string startState, DateTimeOffset started)
        {
            Time = time;
            Started = started;
            StartState = startState;
        }

        public TimeProvider Time { get; }

        public DateTimeOffset Started { get; }

        public string StartState { get; }

        public string Path { get; set; } = "none";

        public bool Retried { get; set; }

        public List<string> Progress { get; } = new();

        public ConnectFailure Failure { get; set; } = ConnectFailure.None;

        // The allow was refused or failed outright. Connect in Windows makes the same allow, so it is not offered then.
        public bool AllowRefused { get; set; }

        public TimeSpan Remaining
        {
            get
            {
                TimeSpan left = Started + ConnectBudget - Time.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        public bool RoomToRetry => Remaining >= RetryRoom;
    }

    private async Task<ToggleReport> ConnectCoreAsync(ToggleRequest request, DateTimeOffset accepted, CancellationToken ct)
    {
        var attempt = new ConnectAttempt(_time, StartStateOf(FreshStatus ?? _blockStatus), accepted);
        using var limit = new CancellationTokenSource(attempt.Remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        ToggleReport report = await ConnectBodyAsync(request, attempt, linked.Token, ct, limit.Token);
        return SealConnect(request, attempt, report);
    }

    // The report with what the attempt learnt added, the Fix offer counted and the history line written.
    private ToggleReport SealConnect(ToggleRequest request, ConnectAttempt attempt, ToggleReport report)
    {
        ConnectFailure failure = report.IsSuccess ? ConnectFailure.None : attempt.Failure;
        bool offer = _services.FixOffer.NoteConnect(report.IsSuccess, failure.Class, report.Cancelled);
        bool failed = !report.IsSuccess && !report.Cancelled;
        ToggleReport sealedReport = report with
        {
            FailureClass = failure.Class,
            Progress = attempt.Progress.ToList(),
            Retried = attempt.Retried,
            OfferConnectInWindows = failed && failure.Class != FailureClass.None && !attempt.AllowRefused,
            FixOfferDue = offer,
        };

        string outcome = report.Cancelled ? ConnectHistoryOutcomes.Cancelled : report.IsSuccess ? ConnectHistoryOutcomes.Connected : ConnectHistoryOutcomes.Failed;
        WriteHistory(new ConnectHistoryEntry(
            attempt.Started, ConnectHistoryActions.Connect, attempt.StartState, attempt.Path, ConnectHistory.FromSteps(report.Steps),
            attempt.Retried, outcome, failure.Class.ToString(), (long)(_time.GetUtcNow() - attempt.Started).TotalMilliseconds));
        return sealedReport;
    }

    private void WriteHistory(ConnectHistoryEntry entry)
    {
        if (_services.History is not { } history)
        {
            return;
        }

        StepOutcome written = history.Append(entry);
        if (!written.Ok)
        {
            _log.Warn("The connect history was not written: " + TrayReport.DescribeStep(written));
        }
    }

    // The state of the nodes as the last read showed it, in words a history line can carry.
    private static string StartStateOf(BootBlockStatus? status)
    {
        if (status is null)
        {
            return "unknown";
        }

        bool away = status.Nodes.Any(n => !n.IsPresent);
        return status.State switch
        {
            BlockState.Allowed => away ? "away" : "present-enabled",
            BlockState.Blocked => away ? "away-flagged" : "present-disabled",
            BlockState.Mixed => "mixed",
            BlockState.NotSetUp => "not-set-up",
            BlockState.NotFound => "none-paired",
            _ => "unknown",
        };
    }

    private void Progress(ToggleRequest request, ConnectAttempt attempt, string status)
    {
        attempt.Progress.Add(status);
        if (!_options.SafeMode)
        {
            ShowCard(request, status);
        }
    }

    // One request, waiting at most what is left of the limit for the endpoint to reach ACTIVE.
    private async Task<ConnectResult> ConnectOnceAsync(ToggleRequest request, ConnectAttempt attempt, List<StepOutcome> steps, SwitchPhase phase, CancellationToken linked)
    {
        linked.ThrowIfCancellationRequested();
        long mark = request.Timeline?.Mark() ?? -1;
        TimeSpan wait = attempt.Remaining < ConfirmationWaiter.TimeoutFor(ConnectAction.Connect) ? attempt.Remaining : ConfirmationWaiter.TimeoutFor(ConnectAction.Connect);
        ConnectResult result = await _connection.ConnectAsync(request.Container, wait, linked);
        request.Timeline?.Record(phase, mark);
        steps.AddRange(result.Steps);
        return result;
    }

    private async Task<ToggleReport> ConnectBodyAsync(
        ToggleRequest request, ConnectAttempt attempt, CancellationToken linked, CancellationToken ct, CancellationToken limitToken)
    {
        // Shown once the connect is under way, not while it waits behind an operation it may never follow. Safe mode refuses
        // the connect, so no card promises it.
        Progress(request, attempt, TrayStatus.CardConnecting);

        var steps = new List<StepOutcome>();
        var cleanup = new ConnectCleanup();
        request.Timeline?.CoreBegan();
        ReArmIdleRule("a connect was asked for");
        try
        {
            ConnectResult result = await ConnectOnceAsync(request, attempt, steps, SwitchPhase.FirstPass, linked);
            if (result.Confirmed)
            {
                attempt.Path = "direct";
                NoteConfirmed(request, result, SwitchPath.Direct);
                return await ConnectedAsync(request, steps, allowed: false, ct);
            }

            if (result.Outcome == ConnectOutcome.NodesBlocked)
            {
                attempt.Path = "allow-first";
                (ToggleReport? early, ConnectResult? afterAllow) = await AllowThenConnectAsync(request, attempt, cleanup, steps, linked, ct, limitToken);
                if (early is not null)
                {
                    return early;
                }

                result = afterAllow!;
                if (result.Confirmed)
                {
                    NoteConfirmed(request, result, SwitchPath.AllowFirst);
                    return await ConnectedAsync(request, steps, allowed: true, ct);
                }
            }

            // A temporary failure is tried once more when the time allows; whatever follows is judged with the retry spent.
            ConnectFailure first = ConnectFailureClassifier.Classify(result, _services.Hints.Read(), retried: false);
            if (first.IsTemporary && !attempt.Retried && attempt.RoomToRetry)
            {
                attempt.Retried = true;
                Progress(request, attempt, TryingAgainStatus);
                result = await ConnectOnceAsync(request, attempt, steps, SwitchPhase.Connect, linked);
                if (result.Confirmed)
                {
                    NoteConfirmed(request, result, attempt.Path == "allow-first" ? SwitchPath.AllowFirst : SwitchPath.Direct);
                    return await ConnectedAsync(request, steps, allowed: cleanup.AllowIssued, ct);
                }
            }

            // Only on observations: the A2DP filter was sent the request and refused it, a good read shows render not
            // ACTIVE, and a good read shows the services protected.
            if (CoordinatorRules.A2dpRejected(result) &&
                await RenderNotActiveAsync(request.Container) &&
                await ProtectionIsOnAsync(ct))
            {
                attempt.Path = "hands-free-assisted";
                return await HandsFreeAssistedConnectAsync(request, attempt, steps, cleanup, ct);
            }

            return await FailAsync(request, attempt, cleanup, steps, ConnectFailureClassifier.Classify(result, _services.Hints.Read(), retried: true), ct);
        }
        catch (Exception ex)
        {
            // A connect cancelled once its walk to the filters began carries what was sent to which filter.
            if (ex is ConnectCancelledException sent)
            {
                steps.AddRange(sent.Steps);
            }

            bool cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            bool limitReached = ex is OperationCanceledException && limitToken.IsCancellationRequested && !ct.IsCancellationRequested;
            if (cancelled)
            {
                _log.Info("connect: cancelled; cleaning up before it ends.");
            }
            else if (limitReached)
            {
                _log.Warn("connect: not connected within " + Seconds(ConnectBudget) + "; cleaning up before it is reported.");
            }
            else
            {
                _log.Error("connect: unexpected error; cleaning up before it is reported.", ex);
            }

            if (limitReached)
            {
                // The limit cut a wait that had been going: the request was taken and nothing came of it.
                var timedOut = new ConnectResult(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting, []);
                return await FailAsync(request, attempt, cleanup, steps, ConnectFailureClassifier.Classify(timedOut, _services.Hints.Read(), retried: true), ct);
            }

            await CleanUpConnectAsync(request, cleanup, steps);
            if (cancelled)
            {
                return CancelledReport(connect: true, OpStatus.Failed, "", steps);
            }

            throw;
        }
    }

    // Steps 2 of the connect: the allow, the wait for a render endpoint, and the request. Returns a finished report when the
    // connect ended here, otherwise the result of the request after the allow.
    private async Task<(ToggleReport? Early, ConnectResult? Result)> AllowThenConnectAsync(
        ToggleRequest request, ConnectAttempt attempt, ConnectCleanup cleanup, List<StepOutcome> steps, CancellationToken linked, CancellationToken ct,
        CancellationToken limitToken)
    {
        if (PinMayMove)
        {
            // The allow would enable whichever device the gate pins when it runs, which may be another one.
            _log.Warn("connect: nothing is allowed, because " + PinMayMoveReason + " until " + Utc(_pinSettlesAt) + ".");
            steps.Add(StepOutcomes.NotAttempted("connect-allow", "Not sent, because " + PinMayMoveReason + "."));
            return (Finish(request, OpStatus.Failed, ChangeStillRunningMessage, steps), null);
        }

        long mark = request.Timeline?.Mark() ?? -1;
        BootBlockStatus? status = await ReadBlockStatusAsync(linked);
        request.Timeline?.Record(SwitchPhase.Status, mark);
        string? refusal = CoordinatorRules.AllowFirstRefusal(status, request.Container);
        if (refusal is not null)
        {
            _log.Warn("connect: every render endpoint is NOTPRESENT and the nodes read " + (status is null ? "nothing" : Describe(status)) +
                " for container " + request.Container.ToString("D") + ", so nothing is allowed.");
            attempt.Failure = new ConnectFailure(FailureClass.PcSide, refusal, "allow-refused");
            attempt.AllowRefused = true;
            return (Finish(request, OpStatus.Failed, refusal, steps), null);
        }

        // The allow cannot be taken back once sent, so a cancellation that came while the status was read ends the connect
        // before it.
        ct.ThrowIfCancellationRequested();
        cleanup.BlockAtBoot = status!.BlockAtBootKnown ? status.BlockAtBoot : null;
        Progress(request, attempt, AllowingStatus);
        cleanup.AllowIssued = true;
        mark = request.Timeline?.Mark() ?? -1;
        ControllerResult allow = await SendAllowAsync("allow (connect)", steps, limitToken);
        request.Timeline?.Record(SwitchPhase.Allow, mark);
        cleanup.AllowNonce = allow.GateNonce;
        if (IsSessionEndRefusal(allow))
        {
            // Nothing was sent, so there is no allow to put back; whatever else the connect changed still is.
            cleanup.AllowIssued = false;
            await CleanUpConnectAsync(request, cleanup, steps);
            return (RefusedAtSessionEnd(request, steps), null);
        }

        if (allow.Status is OpStatus.Failed or OpStatus.NotAttempted)
        {
            if (limitToken.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The budget ended while the gate's run was waited for: reported as the time limit, like any other wait it cut.
                throw new OperationCanceledException(limitToken);
            }

            attempt.AllowRefused = true;
            return (await FailAsync(request, attempt, cleanup, steps, ConnectFailureClassifier.AllowFailed(allow), ct), null);
        }

        // The nodes are in the tree now, or on their way: from here the wait is for the endpoints, and it is cut by the limit.
        mark = request.Timeline?.Mark() ?? -1;
        await ReadBlockStatusAsync(linked);
        Progress(request, attempt, WaitingStatus);
        bool endpointCame = await WaitForSnapshotAsync(request.Container, CoordinatorRules.RenderEndpointPresent, Shorter(EndpointWait, attempt.Remaining), ct);
        if (!endpointCame && attempt.RoomToRetry)
        {
            attempt.Retried = true;
            Progress(request, attempt, TryingAgainStatus);
            endpointCame = await WaitForSnapshotAsync(request.Container, CoordinatorRules.RenderEndpointPresent, Shorter(EndpointWait, attempt.Remaining), ct);
        }

        request.Timeline?.Record(SwitchPhase.Endpoints, mark);
        if (!endpointCame)
        {
            _log.Warn("connect: no render endpoint came back within " + Seconds(EndpointWait) + " of the allow" + (attempt.Retried ? ", twice." : "."));
            ConnectFailure failure = allow.Status == OpStatus.Partial
                ? ConnectFailureClassifier.AllowFailed(allow)
                : ConnectFailureClassifier.EndpointNotBack(retried: true);
            return (await FailAsync(request, attempt, cleanup, steps, failure, ct), null);
        }

        Progress(request, attempt, TrayStatus.CardConnecting);
        ConnectResult result = await ConnectOnceAsync(request, attempt, steps, SwitchPhase.Connect, linked);
        return (null, result);
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // A connect that did not reach ACTIVE: clean up, then report what is true once the clean-up is done. A link that came up
    // meanwhile (in use after all) is a connection.
    private async Task<ToggleReport> FailAsync(
        ToggleRequest request, ConnectAttempt attempt, ConnectCleanup cleanup, List<StepOutcome> steps, ConnectFailure failure, CancellationToken ct)
    {
        ConnectCleanUpOutcome outcome = await CleanUpConnectAsync(request, cleanup, steps);
        NoteBlockedAgain(request, outcome);
        if (outcome == ConnectCleanUpOutcome.InUse)
        {
            return await ConnectedAsync(request, steps, allowed: cleanup.AllowIssued, ct);
        }

        attempt.Failure = failure;
        return Finish(request, OpStatus.Failed, failure.Reason, steps);
    }

    // ---- Connect in Windows ----

    // The AirPods could not be connected from here. Allow the nodes (the same allow a connect makes), open Windows' Bluetooth
    // settings page, and leave the nodes alone for WindowsConnectWindowLength: a link that forms in that time is the owner's
    // and is not cut. The idle rule starts again, from a fresh grace, when the window ends.
    // https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
    public Task<ToggleReport> ConnectInWindowsAsync(ToggleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<ToggleReport>? refused = () => RefusedAtSessionEnd(request);
        if (_sessionEnding)
        {
            _log.Info("connect in windows: not started, because the session is ending.");
            return Task.FromResult(refused());
        }

        return RunExclusiveAsync("connect in windows", token => ConnectInWindowsCoreAsync(request, token), ct, refused);
    }

    private async Task<ToggleReport> ConnectInWindowsCoreAsync(ToggleRequest request, CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        DateTimeOffset started = _time.GetUtcNow();
        string startState = StartStateOf(FreshStatus ?? _blockStatus);
        ReArmIdleRule("a connect in Windows was asked for");
        ToggleReport report;
        try
        {
            report = await AllowAndOpenSettingsAsync(request, steps, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report = CancelledReport(connect: true, OpStatus.Failed, "", steps);
        }

        WriteHistory(new ConnectHistoryEntry(
            started, ConnectHistoryActions.ConnectInWindows, startState, "windows", ConnectHistory.FromSteps(steps), false,
            report.IsSuccess ? ConnectHistoryOutcomes.Allowed : report.Cancelled ? ConnectHistoryOutcomes.Cancelled : ConnectHistoryOutcomes.Failed,
            report.IsSuccess || report.StartPairAirPods ? "None" : FailureClass.PcSide.ToString(), (long)(_time.GetUtcNow() - started).TotalMilliseconds));
        return report;
    }

    private async Task<ToggleReport> AllowAndOpenSettingsAsync(ToggleRequest request, List<StepOutcome> steps, CancellationToken ct)
    {
        BootBlockStatus? status = await ReadBlockStatusAsync(ct);

        // Not set up: no gate exists and nothing of Earshot's blocks the nodes, so there is nothing to allow and no idle rule to
        // keep off. A status whose tasks could not be read is not "not set up", and keeps its refusal.
        if (status is { State: BlockState.NotSetUp, TasksKnown: true })
        {
            return await OpenSettingsBeforeSetUpAsync(request, steps, ct);
        }

        if (status is null or { State: BlockState.NotSetUp or BlockState.NotFound or BlockState.Unknown })
        {
            return Finish(request, OpStatus.Failed, CoordinatorRules.AllowFirstRefusal(status, request.Container) ?? NotAvailableMessage, steps);
        }

        if (status is { State: BlockState.Blocked or BlockState.Mixed })
        {
            if (PinMayMove)
            {
                steps.Add(StepOutcomes.NotAttempted("connect-allow", "Not sent, because " + PinMayMoveReason + "."));
                return Finish(request, OpStatus.Failed, ChangeStillRunningMessage, steps);
            }

            string? refusal = CoordinatorRules.AllowFirstRefusal(status, request.Container);
            if (refusal is not null)
            {
                return Finish(request, OpStatus.Failed, refusal, steps);
            }

            ct.ThrowIfCancellationRequested();
            ShowCard(request, AllowingStatus);
            ControllerResult allow = await SendAllowAsync("allow (connect in windows)", steps, CancellationToken.None);
            if (IsSessionEndRefusal(allow))
            {
                return RefusedAtSessionEnd(request, steps);
            }

            if (allow.Status is OpStatus.Failed or OpStatus.NotAttempted)
            {
                return Finish(request, OpStatus.Failed, ConnectFailureClassifier.AllowFailed(allow).Reason, steps);
            }

            await ReadBlockStatusAsync(ct);
        }

        StepOutcome opened = _services.Launcher.OpenBluetoothSettings();
        steps.Add(opened);
        if (!opened.Ok)
        {
            _log.Warn("connect in windows: " + TrayReport.DescribeStep(opened));
            return Finish(request, OpStatus.Failed, ConnectInWindowsFailedToOpenMessage, steps);
        }

        OpenWindowsConnectWindow();
        return Finish(request, OpStatus.Success, ConnectInWindowsMessage, steps);
    }

    // Connect in Windows before Earshot is set up. With no AirPods paired the tray is told to start Pair AirPods (no card is shown
    // for this report); otherwise Windows' Bluetooth settings page is opened with no allow and no Connect in Windows window.
    private async Task<ToggleReport> OpenSettingsBeforeSetUpAsync(ToggleRequest request, List<StepOutcome> steps, CancellationToken ct)
    {
        PairedAirPodsReading paired = await _services.PairedAirPods.ReadAsync(ct);
        if (paired.NonePaired)
        {
            _log.Info("connect in windows: Earshot is not set up and no AirPods are paired, so Pair AirPods is offered instead.");
            return Finish(request, OpStatus.Failed, NoAirPodsPairedMessage, steps, showCard: false) with { StartPairAirPods = true };
        }

        StepOutcome opened = _services.Launcher.OpenBluetoothSettings();
        steps.Add(opened);
        if (!opened.Ok)
        {
            _log.Warn("connect in windows: " + TrayReport.DescribeStep(opened));
            return Finish(request, OpStatus.Failed, ConnectInWindowsFailedToOpenMessage, steps);
        }

        _log.Info("connect in windows: Earshot is not set up, so nothing was allowed and Windows' Bluetooth settings is open.");
        return Finish(request, OpStatus.Success, BluetoothSettingsOpenMessage, steps);
    }

    private void OpenWindowsConnectWindow()
    {
        CancelWindowTimer();
        _windowsConnectUntil = _time.GetUtcNow() + WindowsConnectWindowLength;
        var timer = new CancellationTokenSource();
        _windowTimer = timer;
        _log.Info("A Connect in Windows window is open until " + Utc(_windowsConnectUntil) + ": a link that forms is the owner's.");
        _ = WindowEndAsync(timer);
    }

    private void CancelWindowTimer()
    {
        CancellationTokenSource? timer = _windowTimer;
        _windowTimer = null;
        timer?.Cancel();
    }

    private async Task WindowEndAsync(CancellationTokenSource timer)
    {
        try
        {
            try
            {
                await Task.Delay(WindowsConnectWindowLength, _time, timer.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested)
            {
                return;
            }

            if (timer.IsCancellationRequested || !ReferenceEquals(_windowTimer, timer) || _disposed || _closing)
            {
                return;
            }

            _windowTimer = null;
            _windowsConnectUntil = DateTimeOffset.MinValue;
            _log.Info("The Connect in Windows window ended; the idle rule starts again.");
            EvaluateIdle();
        }
        catch (Exception ex)
        {
            LogReadFailure("windows-connect-window", "The end of the Connect in Windows window could not be handled.", ex);
        }
        finally
        {
            timer.Dispose();
        }
    }

    // ---- who made the link ----

    // The link is the owner's when it first appears with the session unlocked, and it stays the owner's until it drops, so
    // locking the PC while listening cuts nothing. A link that first appears while the session is locked or its state cannot
    // be read is not, whether or not a Connect in Windows window or a connect of Earshot's own is open: someone at the PC
    // made those unlocked, and a link made after the lock is not theirs.
    private void NoteLinkOwner(DeviceSnapshot snapshot)
    {
        RenderState render = CoordinatorRules.RenderOf(snapshot, WatchedContainer());
        if (render == RenderState.NotActive)
        {
            _ownerLink = false;
            return;
        }

        if (render != RenderState.Active)
        {
            return;
        }

        // Playing settles any doubt about the link bit.
        _unconfirmedSince = null;
        if (_ownerLink)
        {
            return;
        }

        if (_services.Session.Current != SessionLockState.Unlocked)
        {
            return;
        }

        bool ownConnect = _current is not null && _currentName is "connect" or "connect in windows";

        _ownerLink = true;
        if (!ownConnect)
        {
            WriteHistory(new ConnectHistoryEntry(
                _time.GetUtcNow(), ConnectHistoryActions.WindowsConnection, StartStateOf(FreshStatus ?? _blockStatus), "windows", [], false,
                ConnectHistoryOutcomes.Connected, "None", 0));
        }
    }

    // A link is up and was not made for the owner: it formed while the session was locked or unreadable, outside every
    // window Earshot opened.
    private bool IsForeignLink() =>
        !_ownerLink && _services.Session.Current != SessionLockState.Unlocked &&
        SnapshotRender(WatchedContainer()) == RenderState.Active;
}
