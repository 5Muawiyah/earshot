using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Pairing;
using Earshot.Repair;
using Earshot.Tray;

namespace Earshot.App;

// Pair AirPods, from the tray menu, the settings page, a Connect in Windows that finds nothing paired, and the card shown when
// no AirPods are paired at all. It runs in the tray, unelevated (pairing is per user), through the pairing session Fix
// connection's re-pair shares: the radio is read first, the person presses Look for AirPods, picks the device and presses Pair,
// and a pair that passes the model check is pinned and then set up. Nothing is unpaired: a device that is not AirPods is left
// as it is. One run at a time with a Fix connection (FixRunLock) and with an elevated run of the tray's own. A marker covers the
// time between the pairing and the adoption (PairRecoveryMarker): one left by a tray that did not get that far is answered at
// the next start with the stale-pin check and the device picker for the pair it left.
internal sealed partial class TrayContext
{
    internal const string PairRun = "pairing";

    private PairAirPodsServices? _pairServices;

    // The "No AirPods are paired" card is offered once per run of the tray.
    private bool _noAirPodsOffered;

    // True while the start-time look at a pairing that did not finish runs: the run flag stays up for it whatever else ends.
    private bool _recoveringPair;

    private void WirePairAirPods(TrayStartOptions options)
    {
        _pairServices = options.PairAirPods;
        if (_pairServices is not null && !options.DataRootRedirected)
        {
            // A pair made or removed in Windows arrives as a new device group.
            _coordinator.NewDeviceHook = async _ => !await CheckPinnedPairAsync("new device", CardPlace.NearTray);
        }

        _menu.PairAirPodsClicked += (_, _) =>
        {
            _log.Info("Pair AirPods: started from the menu.");
            Start("pair airpods", PairAirPodsAsync);
        };
    }

    internal void PairAirPodsFromSettings()
    {
        _log.Info("Pair AirPods: started from the settings page.");
        StartPairAirPods(CardPlace.NearTray);
    }

    // The card's button, and a report that asks for it.
    private void StartPairAirPods(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        Launch("pair airpods", () => PairAirPodsAsync(place), place);
    }

    // The look at whether the pinned pair is still paired, at tray start and when a new device appeared. Nothing is shown when it
    // is, or when that cannot be told.
    internal void StartPinnedPairCheck(string reason)
    {
        if (_closing || _pairServices is not { } services || _registry.SafeMode)
        {
            return;
        }

        if (reason == "start" && services.Marker.Exists())
        {
            // Set before the coordinator starts its own start checks, which wait while it is.
            _recoveringPair = true;
            services.RunFlag.Active = true;
            Launch("recover a pairing", async () =>
            {
                bool answered = true;
                try
                {
                    answered = await RecoverPairingAsync(services, CardPlace.NearTray);
                }
                finally
                {
                    _recoveringPair = false;
                    services.RunFlag.Active = _elevatedRun == PairRun;
                }

                // The coordinator's check for AirPods Earshot does not protect waited while this ran. When nothing was shown, it
                // runs now: it compares the paired AirPods with the gate's own device, which a pin saved in the tray alone (the
                // block status unread at the adoption) does not change.
                if (!answered && !_closing && !services.RunFlag.Active)
                {
                    _coordinator.CheckProtectionAfterPairRecovery();
                }
            }, CardPlace.NearTray);
            return;
        }

        Launch("check the pinned pair", () => CheckPinnedPairAsync(reason, CardPlace.NearTray), CardPlace.NearTray);
    }

    // A marker from a run that paired and did not adopt. The stale-pin check comes first and says what it found; then a pair of
    // AirPods that is paired and not the pinned one is offered through the device picker, never adopted by itself. The marker
    // stays until a pair is chosen, or until no such pair is left. True when the person was shown something (or Earshot is
    // closing); false when nothing was shown, so the coordinator's own check runs after it.
    private async Task<bool> RecoverPairingAsync(PairAirPodsServices services, CardPlace place)
    {
        _log.Warn("Pair AirPods: an earlier pairing ended before its AirPods were adopted, so they may be paired, unpinned and unblocked.");
        if (!await CheckPinnedPairAsync("pairing recovery", place) || _closing)
        {
            return true;
        }

        AirPodsDiscoveryResult found;
        try
        {
            found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return true;
        }

        // A Pair AirPods pressed while this looked answers for the pairs itself, and a card from here would race its own.
        if (_elevatedRun == PairRun || _closing)
        {
            _log.Info("Pair AirPods: the look at the pairing that did not finish stops, because a pairing has started.");
            return true;
        }

        if (!found.Listed)
        {
            // New AirPods may be paired and unblocked, and nothing can be told now: the person is told, and the marker stays.
            _log.Warn("Pair AirPods: the devices could not be listed, so the person is asked to choose and the marker is kept.");
            OfferChooseLeftPair(PairAirPodsText.RecoverUnread, place);
            return true;
        }

        Guid pinned = _registry.Settings.Current.PinnedContainerId;
        int left = found.Candidates.Count(c => c.DevicePresent && c.ContainerId != pinned);
        if (left == 0)
        {
            ClearPairMarker("no paired AirPods other than the pinned ones are left");
            return false;
        }

        _log.Info("Pair AirPods: " + left.ToString(System.Globalization.CultureInfo.InvariantCulture) + " paired set(s) of AirPods are not the pinned ones, so the device picker is offered.");
        OfferChooseLeftPair(PairAirPodsText.RecoverChoose, place);
        return true;
    }

    private void OfferChooseLeftPair(string line, CardPlace place) =>
        ShowCardWithButton(TrayStatus.AppName, line, PairAirPodsText.ChooseButton, () => Launch("choose device", () => ChooseDeviceAsync(place), place), place);

    // The paired AirPods that are not the pinned ones, by container; null when the device list could not be read.
    private async Task<HashSet<Guid>?> UnpinnedAirPodsAsync(PairAirPodsServices services)
    {
        AirPodsDiscoveryResult found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), CancellationToken.None);
        if (!found.Listed)
        {
            return null;
        }

        Guid pinned = _registry.Settings.Current.PinnedContainerId;
        return found.Candidates.Where(c => c.DevicePresent && c.ContainerId != pinned).Select(c => c.ContainerId).ToHashSet();
    }

    // After a run that adopted nothing: whether it left a new pair of AirPods paired and not pinned. A pairing that timed out,
    // threw or was cancelled may still have completed in Windows, so the sets before and after are compared rather than the
    // run's own word trusted. Not being able to tell is its own answer, so the card says only that, and the marker is kept.
    private async Task<LeftPair> LeftANewPairAsync(PairAirPodsServices services, HashSet<Guid>? before)
    {
        HashSet<Guid>? after = await UnpinnedAirPodsAsync(services);
        if (before is null || after is null)
        {
            return LeftPair.Unknown;
        }

        return after.Except(before).Any() ? LeftPair.Left : LeftPair.None;
    }

    private enum LeftPair
    {
        None,
        Left,
        Unknown,
    }

    // The card for a run that may have left a pair: what was seen, and the device picker.
    private void OfferLeftPair(LeftPair left, CardPlace place) =>
        OfferChooseLeftPair(left == LeftPair.Left ? PairAirPodsText.LeftPaired : PairAirPodsText.LeftUnread, place);

    // The marker this run wrote, cleared when the run ended with nothing to answer for. A marker an earlier run left is kept for
    // the next start's recovery, which looks at what that run left.
    private void ClearOwnMarker(bool marked, bool earlierMarker, string why)
    {
        if (!marked)
        {
            return;
        }

        if (earlierMarker)
        {
            _log.Info("Pair AirPods: the recovery marker is kept (" + why + "), because an earlier pairing left it and the next start looks at that one.");
            return;
        }

        ClearPairMarker(why, fromPairRun: true);
    }

    private void ClearPairMarker(string why, bool fromPairRun = false)
    {
        if (_pairServices is not { } services || !services.Marker.Exists())
        {
            return;
        }

        // The marker of a pairing under way belongs to that run: only the run itself clears it.
        if (_elevatedRun == PairRun && !fromPairRun)
        {
            _log.Info("Pair AirPods: the recovery marker is not cleared (" + why + "), because a pairing is under way.");
            return;
        }

        StepOutcome cleared = services.Marker.Clear();
        _log.Write(cleared.Ok ? LogLevel.Info : LogLevel.Warn, "Pair AirPods: recovery marker cleared (" + why + "): " + cleared.CodeName + " " + cleared.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
    }

    // Whether the AirPods Earshot has pinned are still paired, read only. True means go on: the pin is fine, or nothing could be
    // told (not knowing is never stale). False means the person has been told what to do and the caller must not go on: a stale
    // pin forgets the replaced pair's readings and link, then offers the device picker when another pair is paired, or Pair
    // AirPods when none is. The other pair is never adopted by itself, and nothing is unpaired. Nothing pinned with no AirPods
    // paired at all offers Pair AirPods too (once per run, unless the person has just asked for something that needs a pair).
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothdevice.getdeviceselectorfrompairingstate
    private async Task<bool> CheckPinnedPairAsync(string reason, CardPlace place)
    {
        if (_closing || _pairServices is not { } services || _registry.SafeMode)
        {
            return true;
        }

        // A pairing of the tray's own is under way: the device group it makes arrives before its adoption, and a card about a
        // stale pin or an unprotected pair would race the pairing's own result.
        if (_elevatedRun == PairRun)
        {
            _log.Info("Pairing check (" + reason + "): skipped while Pair AirPods runs.");
            return true;
        }

        EarshotSettings settings = _registry.Settings.Current;
        StalePinResult result;
        try
        {
            result = await StalePin.ReadAsync(services.Api, services.Nodes, settings.PinnedContainerId, settings.PinnedAddress, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return true;
        }

        _log.Info("Pairing check (" + reason + "): " + StalePin.Describe(result));
        if (_closing)
        {
            return true;
        }

        switch (result.State)
        {
            case StalePinState.NotPinned:
            {
                AirPodsDiscoveryResult found;
                try
                {
                    found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return true;
                }

                bool nonePaired = found.Listed && !found.Candidates.Any(c => c.DevicePresent);
                if (nonePaired)
                {
                    OfferPairAirPods(place, force: reason == "setup");
                }

                return !nonePaired;
            }

            case StalePinState.Stale:
                _log.Info("The pinned AirPods are no longer paired, so their last readings and link are dropped.");
                _widgetStatus?.ForgetPair("the pinned AirPods are no longer paired");
                if (result.Others.Count == 0)
                {
                    OfferPairAirPods(place, force: true);
                }
                else
                {
                    ShowCardWithButton(
                        TrayStatus.AppName, PairAirPodsText.StaleChosen, PairAirPodsText.ChooseButton,
                        () => Launch("choose device", () => ChooseDeviceAsync(place), place), place);
                }

                return false;

            default:
                return true;
        }
    }

    // A card with one button, in the same place as the other cards of the tray.
    private void ShowCardWithButton(string title, string text, string label, Action onClick, CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        var content = new CardContent(title, text, new CardAction(label, onClick));
        if (place.ClickPoint is { } point)
        {
            _registry.Cards.Show(content, place.Anchor, point);
        }
        else
        {
            _registry.Cards.Show(content, place.Anchor);
        }
    }

    // One radio read (its own budget is a second), timed in the log. Null when there is no radio check to ask or in safe mode.
    private async Task<RadioCheckResult?> ReadRadioAfterFailureAsync(CancellationToken ct)
    {
        if (_pairServices?.Radio is not { } radio || _registry.SafeMode)
        {
            return null;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RadioCheckResult result = await radio.CheckAsync(ct);
        _log.Info("Radio check after a failed connect: " + result.State + " in " + watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        return result;
    }

    // The radio's line, and its button when it has one.
    private void ShowRadioCard(RadioCheckResult radio, CardPlace place)
    {
        string line = radio.Line ?? "";
        switch (radio.Action)
        {
            case RadioAction.BluetoothSettings:
                ShowCardWithButton(TrayStatus.AppName, line, RadioLines.BluetoothSettingsButton, () => Launch("open bluetooth settings", () => OpenBluetoothSettingsAsync(place), place), place);
                break;
            case RadioAction.RecordTrace when TraceOffered:
                ShowCardWithButton(TrayStatus.AppName, line, RadioLines.RecordTraceButton, () => StartTrace(place), place);
                break;
            default:
                ShowCard(TrayStatus.AppName, line, place);
                break;
        }
    }

    // "No AirPods are paired with this PC." and the button that starts Pair AirPods. Once per run unless forced.
    private void OfferPairAirPods(CardPlace place, bool force = false)
    {
        if (_closing || _closed || (_noAirPodsOffered && !force) || _pairServices is null || _registry.SafeMode || _elevatedRun == PairRun)
        {
            return;
        }

        _noAirPodsOffered = true;
        _log.Info("No AirPods are paired with this PC, so Pair AirPods is offered.");
        ShowCardWithButton(TrayStatus.AppName, PairAirPodsText.NoAirPodsPaired, PairAirPodsText.Title, () => StartPairAirPods(place), place);
    }

    private async Task PairAirPodsAsync(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        if (_registry.SafeMode)
        {
            _log.Info("Pair AirPods: refused in safe mode.");
            ShowCard(TrayStatus.AppName, MenuModel.SafeMode, place);
            return;
        }

        if (_pairServices is not { } services)
        {
            _log.Warn("Pair AirPods: this tray was started without the pairing services.");
            ShowCard(TrayStatus.AppName, PairAirPodsText.NotAvailable, place);
            return;
        }

        if (!TryBeginElevatedRun(PairRun, place))
        {
            return;
        }

        PairRunEnd end;
        var setupSteps = new List<StepOutcome>();
        services.RunFlag.Active = true;
        try
        {
            end = await PairAndAdoptAsync(services, place);
            if (end.Early is null && end.Session is not null && end.RunSetUp && !_closing)
            {
                // The run is handed straight from the pairing to set-up, so nothing else (a Fix, an update) can start in
                // between. The pin was made a moment ago, so set-up's own look at whether it is still paired is not needed.
                _elevatedRun = SetupRun;
                UpdatePresentation(forceIcon: false);
                var setupWatch = System.Diagnostics.Stopwatch.StartNew();
                await RunSetupClaimedAsync(place);
                BootBlockStatus? after = await ReadBlockStatusQuietlyAsync();
                bool done = after is not null && !TrayStatus.NeedsSetUp(after);
                setupSteps.Add(new StepOutcome("setup", done, done ? 0 : NativeCodes.NotAttempted, done ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null));
                setupSteps.Add(Milliseconds("setup-ms", setupWatch.ElapsedMilliseconds));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run still leaves its one history entry, with the type and code of what stopped it; the caller logs it.
            _coordinator.WriteHistory(EndedEarly(
                StepOutcomes.FromHResult("unexpected", ex.HResult, ex.GetType().Name, ok: false), ConnectHistoryOutcomes.Failed, 0).Early!);
            throw;
        }
        finally
        {
            EndElevatedRun(PairRun);
            EndElevatedRun(SetupRun);
            services.RunFlag.Active = _recoveringPair;
        }

        if (end.Early is { } early)
        {
            _coordinator.WriteHistory(early);
            return;
        }

        if (end.Session is null)
        {
            return;
        }

        // Each step after the session's own, with its code and how long it took.
        var later = new List<StepOutcome>();
        if (end.Replace is { } replaceStep)
        {
            later.Add(replaceStep);
        }

        if (end.Adopted is { } adopted && end.Replace is not { Ok: false })
        {
            later.Add(end.Adoption switch
            {
                DeviceChoiceResult.Applied => new StepOutcome("adopt", true, 0, "S_OK", null),
                DeviceChoiceResult.AppliedGateNotRepinned => new StepOutcome("adopt", false, 1, "gate-not-repinned", null),
                _ => new StepOutcome("adopt", adopted, adopted ? 0 : NativeCodes.NotAttempted, adopted ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null),
            });
            later.Add(Milliseconds("adopt-ms", end.AdoptMs));
        }

        later.AddRange(setupSteps);
        _coordinator.WriteHistory(end.Session.ToHistoryEntry(_time.GetUtcNow(), later));
    }

    // The one history entry of a run that ended before the session gave one: the step it stopped at, with its code and the time.
    private PairRunEnd EndedEarly(StepOutcome stoppedAt, string outcome, long ms) => new(null, null, false)
    {
        Early = new ConnectHistoryEntry(
            _time.GetUtcNow(), ConnectHistoryActions.Pair, "none", "custom-pairing",
            ConnectHistory.FromSteps([stoppedAt, Milliseconds(stoppedAt.Step + "-ms", ms)]),
            false, outcome, FailureClass.None.ToString(), ms),
    };

    // A step that carries a duration: the code is the milliseconds, as the session's scan-ms step does.
    internal static StepOutcome Milliseconds(string step, long ms) => new(step, true, (int)Math.Min(ms, int.MaxValue), "ms", null);

    private async Task<BootBlockStatus?> ReadBlockStatusQuietlyAsync()
    {
        try
        {
            return await _coordinator.ReadBlockStatusAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
    }

    // What one run left for the caller: the session (null when nothing ran), whether the pair was adopted (null when no pair
    // was to be adopted), and whether set-up is to follow.
    private sealed record PairRunEnd(PairingSessionResult? Session, bool? Adopted, bool RunSetUp)
    {
        public static PairRunEnd Nothing { get; } = new(null, null, false);

        // The history entry of a run that ended before or outside the session (refused by the lock, Look for AirPods not pressed,
        // the marker not written, or Cancel pressed in the window), which has no session result to take it from.
        public ConnectHistoryEntry? Early { get; init; }

        // How long the adoption took and what it did, for its step in the history.
        public long AdoptMs { get; init; }

        public DeviceChoiceResult? Adoption { get; init; }

        // The answer to "use these instead?", when it was asked.
        public StepOutcome? Replace { get; init; }
    }

    private async Task<PairRunEnd> PairAndAdoptAsync(PairAirPodsServices services, CardPlace place)
    {
        using FixRunLock? runLock = FixRunLock.TryAcquire(services.LockName, _log);
        if (runLock is null)
        {
            _log.Info("Pair AirPods: not started, the Fix lock is held (by a Fix connection or pairing run, or by another program holding its name).");
            ShowCard(TrayStatus.AppName, FixRunLock.PairAlreadyRunningMessage, place);
            return EndedEarly(new StepOutcome("lock", false, ExitCodes.AlreadyRunning, "already-running", null), ConnectHistoryOutcomes.Failed, 0);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IFixRePairUi ui = services.UiFactory(cancel);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HashSet<Guid>? before = null;
        bool marked = false;

        // A marker an earlier run left is that run's: this run may write over it, but only a recovery that has looked at what it
        // left clears it.
        bool earlierMarker = services.Marker.Exists();
        try
        {
            var session = new PairingSession(services.Api, services.Nodes, ui, services.Options, _log, services.Radio);
            RadioCheckResult? radio = await session.CheckRadioAsync(cancel.Token);
            if (radio is not { BlocksRepair: true })
            {
                var question = new FixPrompt(PairAirPodsText.Title, PairAirPodsText.Title, PairAirPodsText.FirstQuestionBody, [], [], PairAirPodsText.LookButton);
                FixPromptAnswer answer = services.Prompter.Ask(question);
                if (answer.Answer != FixAnswer.Ok)
                {
                    _log.Info("Pair AirPods: the person did not press Look for AirPods, so nothing was scanned.");
                    return EndedEarly(new StepOutcome("look", false, NativeCodes.NotAttempted, answer.Answer.ToString(), null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds);
                }
            }

            // Written before anything can be paired, so the time between the pairing and the adoption is always covered.
            // Without it a crash between the pairing and the adoption would go unnoticed, so no pairing starts when it cannot be written.
            // A radio that stops the pairing pairs nothing, so no marker is needed and its own line is the one shown.
            if (radio is not { BlocksRepair: true })
            {
                before = await UnpinnedAirPodsAsync(services);
                StepOutcome written = services.Marker.Write();
                _log.Write(written.Ok ? LogLevel.Info : LogLevel.Warn, "Pair AirPods: recovery marker written: " + written.CodeName + " " + written.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                if (!written.Ok)
                {
                    ShowCard(TrayStatus.AppName, PairAirPodsText.MarkerNotWritten(written.Code), place);
                    return EndedEarly(written, ConnectHistoryOutcomes.Failed, watch.ElapsedMilliseconds);
                }

                marked = true;
            }

            PairingSessionResult result = await session.RunAsync(excludeAlreadyListed: false, cancel.Token, radio);
            _log.Info("Pair AirPods: " + result.Outcome + ", " + result.Reason + ".");
            if (result.Outcome == PairOutcome.Unverified)
            {
                // Paired and not looked at: maybe AirPods. The marker stays, so the next start looks again, and the picker is
                // offered now; nothing is adopted without the person's choice.
                _log.Warn("Pair AirPods: the paired device could not be checked, so the recovery marker is kept and the device picker is offered.");
                ShowCardWithButton(TrayStatus.AppName, PairingText.Unverified, PairAirPodsText.ChooseButton,
                    () => Launch("choose device", () => ChooseDeviceAsync(place), place), place);
                return new PairRunEnd(result, null, false);
            }

            if (result.Outcome != PairOutcome.Paired || result.Adoptable is not { } found)
            {
                LeftPair left = marked ? await LeftANewPairAsync(services, before) : LeftPair.None;
                if (left != LeftPair.None)
                {
                    // Windows may have finished a pairing the session gave up on, or what is paired cannot be read: the picker is
                    // offered with a line that says which, and the marker stays.
                    _log.Warn("Pair AirPods: the run ended " + result.Outcome + " and a new pair of AirPods " + (left == LeftPair.Left ? "is paired and not pinned" : "may be paired (the devices could not be read)") + ", so the device picker is offered and the marker kept.");
                    OfferLeftPair(left, place);
                    return new PairRunEnd(result, null, false);
                }

                ClearOwnMarker(marked, earlierMarker, "the pairing left no AirPods to adopt");
                ShowPairResult(result, radio, place);
                return new PairRunEnd(result, null, false);
            }

            // Earshot always asks before it takes on a different pair: the pick and the Pair press chose what to pair, not that it
            // takes the place of the pair Earshot is set to use.
            FixAnswer? replace = AskToReplace(services, found, _registry.Settings.Current.PinnedContainerId);
            if (replace is { } kept && kept != FixAnswer.Ok)
            {
                if (kept == FixAnswer.Cancel && !_closing)
                {
                    _log.Info("Pair AirPods: the person kept the AirPods Earshot uses, so the new pair is left paired and not used.");
                    ClearOwnMarker(marked, earlierMarker, "the person kept the AirPods Earshot uses");
                    ShowCard(TrayStatus.AppName, PairAirPodsText.KeptOtherPair, place);
                }
                else
                {
                    // Not answered (the question timed out, or Earshot is closing): nobody decided, so the new pair is offered and
                    // the marker stays for the next start.
                    _log.Warn("Pair AirPods: whether to use the new pair was not answered (" + (_closing ? "closing" : kept.ToString()) + "), so the marker is kept.");
                    OfferChooseLeftPair(PairAirPodsText.LeftPaired, place);
                }

                return new PairRunEnd(result, false, false)
                {
                    Replace = new StepOutcome("replace", false, NativeCodes.NotAttempted, kept.ToString(), null),
                    Adoption = DeviceChoiceResult.NotApplied,
                };
            }

            var adoptWatch = System.Diagnostics.Stopwatch.StartNew();
            DeviceChoiceResult adoption = await AdoptPairedAirPodsAsync(found, place);
            long adoptMs = adoptWatch.ElapsedMilliseconds;
            if (adoption == DeviceChoiceResult.AppliedGateNotRepinned)
            {
                // Pinned in the tray only: the gate still names the previous pair, so the new one is not blocked at rest. Its own card
                // says so, and the marker stays for the next start's check.
                _log.Warn("Pair AirPods: the boot block status could not be read, so the gate was not given the new pair; the recovery marker is kept.");
                return new PairRunEnd(result, false, false)
                {
                    AdoptMs = adoptMs,
                    Adoption = adoption,
                    Replace = replace is null ? null : new StepOutcome("replace", true, 0, FixAnswer.Ok.ToString(), null),
                };
            }

            bool adopted = adoption == DeviceChoiceResult.Applied;
            if (!adopted)
            {
                _log.Warn("Pair AirPods: the recovery marker is kept, so the next start offers these AirPods again.");
            }

            bool setUp = false;
            if (adopted)
            {
                BootBlockStatus? status = await ReadBlockStatusQuietlyAsync();
                setUp = TrayStatus.NeedsSetUp(status);
            }

            ShowCard(TrayStatus.AppName, !adopted ? PairAirPodsText.PairedNotAdopted : setUp ? PairingText.SetsItselfUp : PairAirPodsText.PairedAndUsing, place);
            return new PairRunEnd(result, adopted, setUp)
            {
                AdoptMs = adoptMs,
                Adoption = adoption,
                Replace = replace is null ? null : new StepOutcome("replace", true, 0, FixAnswer.Ok.ToString(), null),
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The run still leaves its history entry. The marker stays, so the next start looks at whatever was paired.
            _log.Info("Pair AirPods: cancelled because Earshot is closing; the recovery marker is kept.");
            return EndedEarly(new StepOutcome("closing", false, NativeCodes.NotAttempted, "exit", null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The person pressed Cancel in the Pair AirPods window: a cancel, not a fault. Windows may already have paired, so
            // what is paired now decides whether the pair is offered or the marker cleared.
            _log.Info("Pair AirPods: cancelled in its window after " + watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
            (ui as IDisposable)?.Dispose();
            LeftPair left = marked ? await LeftANewPairAsync(services, before) : LeftPair.None;
            if (left != LeftPair.None)
            {
                OfferLeftPair(left, place);
            }
            else
            {
                ClearOwnMarker(marked, earlierMarker, "the pairing was cancelled and left no new AirPods");
                ShowCard(TrayStatus.AppName, PairingText.Cancelled, place);
            }

            return EndedEarly(new StepOutcome("cancelled", false, NativeCodes.NotAttempted, "by-person", null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds);
        }
        finally
        {
            (ui as IDisposable)?.Dispose();
        }
    }

    // The result of a pairing that adopted nothing: the radio's line and button when the radio stopped it, otherwise the
    // session's own one line.
    private void ShowPairResult(PairingSessionResult result, RadioCheckResult? radio, CardPlace place)
    {
        if (result.Reason == PairFailureReason.RadioNotReady && radio is not null)
        {
            ShowRadioCard(radio, place);
            return;
        }

        // The AirPods were found but could not be reached: the one case where a trace may show where it stopped.
        if (result.Unreachable)
        {
            ShowRadioCard(UnreachableCheck, place);
            return;
        }

        ShowCard(TrayStatus.AppName, result.CardLine ?? PairingText.Cancelled, place);
    }

    // What the card needs of a check to say Unreachable when the session ran without one.
    private static readonly RadioCheckResult UnreachableCheck = new(
        RadioCheckState.Unreachable, RadioLines.Unreachable, RadioAction.RecordTrace, null, null, null, null, null, null, null, [], 0);

    // Another pair taking the place of the pinned one drops the previous pair's last readings and link, so the card and the gauge
    // never show the replaced pair's figures under the new one. Nothing is dropped when no valid pair was pinned before, or when
    // the same pair is chosen again.
    private void ForgetPreviousPairIfDifferent(Guid previous, Guid chosen)
    {
        if (!NodeMatch.IsValidTargetContainer(previous) || previous == chosen)
        {
            return;
        }

        _log.Info("A different pair is now the one Earshot uses, so the previous pair's last readings and link are dropped.");
        _widgetStatus?.ForgetPair("a different pair was adopted");
    }

    // Whether the new pair may take the place of the pinned one: null when there is nothing to replace (nothing valid pinned, or
    // the same pair again), otherwise the person's answer. Asked whether or not the pinned pair is still paired: Earshot always
    // asks before it takes on a different pair.
    private static FixAnswer? AskToReplace(PairAirPodsServices services, AirPodsCandidate found, Guid pinned)
    {
        if (!NodeMatch.IsValidTargetContainer(pinned) || pinned == found.ContainerId)
        {
            return null;
        }

        var question = new FixPrompt(PairAirPodsText.Title, PairAirPodsText.ReplaceHeading, PairAirPodsText.ReplaceBody, [], [], PairAirPodsText.ReplaceButton);
        return services.Prompter.Ask(question).Answer;
    }

    // The pair the person has just paired, and agreed to use, is adopted at once: pinned, given to the gate when
    // Earshot is set up, and the previous pair's readings and link dropped.
    private async Task<DeviceChoiceResult> AdoptPairedAirPodsAsync(AirPodsCandidate found, CardPlace place)
    {
        string name = string.IsNullOrWhiteSpace(found.Name) ? "AirPods" : found.Name;
        DeviceChoiceResult applied = await ApplyDeviceChoiceAsync(new PickerChoice(name, name, found.Address, found.ContainerId), place, fromPairRun: true);
        _log.Info("Pair AirPods: the new pair " + applied switch
        {
            DeviceChoiceResult.Applied => "is the one Earshot uses.",
            DeviceChoiceResult.AppliedGateNotRepinned => "is pinned in the tray, and the gate was not given it.",
            _ => "was not adopted.",
        });
        return applied;
    }
}

// What applying a device choice did: nothing, a pin the gate took (or no gate to take it), or a pin in the tray only because the
// boot block status could not be read, so the gate may still name the previous pair.
internal enum DeviceChoiceResult
{
    NotApplied,
    Applied,
    AppliedGateNotRepinned,
}
