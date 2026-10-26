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

    // When the person last kept the pinned pair over a new one. The new pair's device group can arrive just before that answer,
    // and the coordinator's check for unprotected AirPods then fires a few seconds after it; the card the person was just shown
    // already says the new pair is not protected, so for a short while the tray answers for a new device itself. A UI timing
    // choice, not a measurement.
    private DateTimeOffset? _keptPinnedPairAt;
    internal static readonly TimeSpan KeptPairQuiet = TimeSpan.FromSeconds(30);

    private bool KeptThePinnedPairJustNow() => _keptPinnedPairAt is { } at && _time.GetUtcNow() - at < KeptPairQuiet;

    // A start-time look (at a pairing that did not finish, or at the pinned pair) gave way to a Pair AirPods pressed while it
    // looked, so the check for AirPods Earshot does not protect, which waited for that look, is owed once the pairing ends.
    private bool _protectionCheckOwed;

    // The pairing flag is dropped: the coordinator reads the state again now, and a check the look owed runs (not over the line
    // that says the new pair is kept unprotected, just shown). UI thread only.
    private void DropPairRunFlag(PairAirPodsServices services, bool keep)
    {
        services.RunFlag.Active = keep;
        if (keep || _closing)
        {
            return;
        }

        _coordinator.TrayPairingEnded();

        // Owed to a Pair AirPods that is still running (its first question up, its flag not yet raised): it runs when that run
        // ends and drops the flag itself.
        if (_protectionCheckOwed && _elevatedRun != PairRun)
        {
            _protectionCheckOwed = false;
            if (!KeptThePinnedPairJustNow())
            {
                _coordinator.CheckProtectionAfterTrayPairing("a pairing started during the tray's start-time look at the pairs");
            }
        }
    }

    private void WirePairAirPods(TrayStartOptions options)
    {
        _pairServices = options.PairAirPods;
        if (_pairServices is not null && !options.DataRootRedirected)
        {
            // A pair made or removed in Windows arrives as a new device group. A Pair AirPods that runs (its first question up, the
            // flag not yet raised) answers for the pairs itself.
            // A card Windows held back has told nobody anything, so it does not answer for the new device.
            _coordinator.NewDeviceHook = async _ =>
            {
                if (KeptThePinnedPairJustNow())
                {
                    return true;
                }

                var cards = new PinLookCards();
                bool showedNothing = await CheckPinnedPairAsync("new device", CardPlace.NearTray, cards: cards);
                return (!showedNothing && !cards.Held) || _elevatedRun == PairRun;
            };
            _coordinator.StartLookHook = () => _startLookShown?.Task ?? Task.FromResult(false);
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
            // Set before the coordinator starts its own start checks, which wait while it is. The look's answer is given to them
            // too (StartLookHook): a start check that comes only after the flag is dropped (its own answer to a Fix run took
            // longer) must not show its card over this one's.
            _recoveringPair = true;
            services.RunFlag.Active = true;
            var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startLookShown = recovered;
            Launch("recover a pairing", async () =>
            {
                bool answered = true;
                try
                {
                    answered = await RecoverPairingAsync(services, CardPlace.NearTray);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A look that threw showed nothing about the pairs: the error is reported with its code, and the check for
                    // unprotected AirPods below still runs.
                    answered = false;
                    ReportUnexpected("recover a pairing", ex, CardPlace.NearTray);
                }
                finally
                {
                    recovered.TrySetResult(answered);
                    _recoveringPair = false;
                    DropPairRunFlag(services, keep: _pairRunFlagRaised);
                }

                // The coordinator's check for AirPods Earshot does not protect waited while this ran. When nothing was shown, it
                // runs now: it compares the paired AirPods with the gate's own device, which a pin saved in the tray alone (the
                // block status unread at the adoption) does not change.
                if (!answered && !_closing && !services.RunFlag.Active)
                {
                    _coordinator.CheckProtectionAfterTrayPairing("the look at a pairing that did not finish showed nothing");
                }
            }, CardPlace.NearTray);
            return;
        }

        if (reason == "start")
        {
            // The coordinator's start check for unprotected AirPods waits for this look's answer (StartLookHook): a card from
            // here already says what there is to say about the pairs, and the other card would replace it.
            var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startLookShown = shown;
            Launch("check the pinned pair", async () =>
            {
                bool showedNothing = true;
                var cards = new PinLookCards();
                try
                {
                    showedNothing = await CheckPinnedPairAsync(reason, CardPlace.NearTray, cards: cards);
                }
                finally
                {
                    // A card Windows held back (quiet time, a full-screen app at sign-in) was not seen, so it does not answer for
                    // the pairs.
                    shown.TrySetResult(!showedNothing && !cards.Held);
                }
            }, CardPlace.NearTray);
            return;
        }

        Launch("check the pinned pair", () => CheckPinnedPairAsync(reason, CardPlace.NearTray), CardPlace.NearTray);
    }

    // The pairing run has raised the flag itself (Look for AirPods was pressed), so a recovery that ends meanwhile leaves it up.
    private bool _pairRunFlagRaised;

    // The start-time look's answer: true when it showed the person a card about the pairs. Null when no look was started.
    private TaskCompletionSource<bool>? _startLookShown;

    // The gate's first failing step the last time it refused a device choice, for the pairing's adopt step.
    private StepOutcome? _lastGateRefusal;

    // A marker from a run that paired and did not adopt. The stale-pin check comes first and says what it found; then a pair of
    // AirPods that is paired and not the pinned one is offered through the device picker, never adopted by itself. The marker
    // stays until a pair is chosen, or until no such pair is left. True when the person was shown something, Earshot is closing,
    // or a pairing that started meanwhile owes the check; false when nothing was shown, so the coordinator's own check runs
    // after it.
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

        // A Pair AirPods pressed while this looked answers for the pairs itself, and a card from here would race its own. Nothing
        // was shown, so the coordinator's own check is owed once that pairing ends (DropPairRunFlag), and the marker stays.
        if (_elevatedRun == PairRun || _closing)
        {
            _log.Info("Pair AirPods: the look at the pairing that did not finish stops, because a pairing has started.");
            _protectionCheckOwed = !_closing;
            return true;
        }

        if (!found.Listed)
        {
            // New AirPods may be paired and unblocked, and nothing can be told now: the person is told, and the marker stays.
            _log.Warn("Pair AirPods: the devices could not be listed, so the person is asked to choose and the marker is kept.");
            return await OfferChooseLeftPairAsync(PairAirPodsText.RecoverUnread, place);
        }

        Guid pinned = _registry.Settings.Current.PinnedContainerId;
        int left = found.Candidates.Count(c => c.DevicePresent && c.ContainerId != pinned);
        if (left == 0)
        {
            ClearPairMarker("no paired AirPods other than the pinned ones are left");
            return false;
        }

        _log.Info("Pair AirPods: " + left.ToString(System.Globalization.CultureInfo.InvariantCulture) + " paired set(s) of AirPods are not the pinned ones, so the device picker is offered.");
        return await OfferChooseLeftPairAsync(PairAirPodsText.RecoverChoose, place);
    }

    // The same card, completing with whether it was put on screen. Windows may hold a card back at sign-in (quiet time, a
    // full-screen app, the desktop not yet there): the recovery then counts as having shown nothing, so the coordinator's own
    // check still runs, and the marker stays for the next start.
    private async Task<bool> OfferChooseLeftPairAsync(string line, CardPlace place)
    {
        if (_closing || _closed)
        {
            return true;
        }

        var content = new CardContent(
            TrayStatus.AppName, line, new CardAction(PairAirPodsText.ChooseButton, () => Launch("choose device", () => ChooseDeviceAsync(place), place)), StaysUp: true);
        bool shown = await _registry.Cards.ShowAsync(content, place.Anchor, place.ClickPoint);
        if (!shown)
        {
            _log.Info("Pair AirPods: the card offering the device picker was held back by Windows, so the marker is kept for the next start.");
        }

        return shown;
    }

    // The card stays up until it is used or replaced: it is shown at sign-in, while the desktop loads, and while it is missed the
    // new pair stays paired, unpinned and unblocked.
    private void OfferChooseLeftPair(string line, CardPlace place) =>
        ShowCardWithButton(TrayStatus.AppName, line, PairAirPodsText.ChooseButton, () => Launch("choose device", () => ChooseDeviceAsync(place), place), place, staysUp: true);

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
    // token: the caller's own (a connect's), so a press that supersedes it ends this look too; otherwise Earshot's lifetime.
    // cards: told when this look's card was held back by Windows, for a caller that counts only a card that was seen.
    private async Task<bool> CheckPinnedPairAsync(string reason, CardPlace place, CancellationToken? token = null, PinLookCards? cards = null)
    {
        CancellationToken ct = token ?? _lifetime.Token;
        cards ??= new PinLookCards();
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
            result = await StalePin.ReadAsync(services.Api, services.Nodes, settings.PinnedContainerId, ct, services.StalePinBudget);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing, or the connect it was for was superseded: nothing more is shown for it.
            return _lifetime.IsCancellationRequested;
        }

        _log.Info("Pairing check (" + reason + "): " + StalePin.Describe(result));
        if (_closing)
        {
            return true;
        }

        // A Pair AirPods pressed while the paired list was read answers for the pairs itself: a card from here would show over
        // its own, and a choice made on it would race its adoption.
        if (_elevatedRun == PairRun)
        {
            return PairingStartedWhileLooking();
        }

        switch (result.State)
        {
            case StalePinState.NotPinned:
            {
                AirPodsDiscoveryResult found;
                try
                {
                    found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return _lifetime.IsCancellationRequested;
                }

                bool nonePaired = found.Listed && !found.Candidates.Any(c => c.DevicePresent);
                if (nonePaired)
                {
                    switch (await RadioExplainsAsync(reason, place, cards, ct))
                    {
                        case RadioSays.Shown:
                            return false;
                        case RadioSays.Unsure:
                            return true;
                        case RadioSays.Stop:
                            return RadioStopped();
                    }

                    await OfferPairAirPodsAsync(place, cards, force: reason == "setup");
                }

                return !nonePaired;
            }

            case StalePinState.Stale:
                switch (await RadioExplainsAsync(reason, place, cards, ct))
                {
                    case RadioSays.Shown:
                        return false;
                    case RadioSays.Unsure:
                        return true;
                    case RadioSays.Stop:
                        return RadioStopped();
                }

                _log.Info("The pinned AirPods are no longer paired, so their last readings and link are dropped.");
                _widgetStatus?.ForgetPair("the pinned AirPods are no longer paired");
                if (result.Others.Count == 0)
                {
                    await OfferPairAirPodsAsync(place, cards, force: true);
                }
                else
                {
                    // Stays up until it is used: it is often shown at sign-in, and until a pair is chosen none is protected.
                    await ShowStayingCardAsync(
                        PairAirPodsText.StaleChosen, PairAirPodsText.ChooseButton, () => Launch("choose device", () => ChooseDeviceAsync(place), place), place, cards);
                }

                return false;

            default:
                return true;
        }

        // A Pair AirPods pressed while the look read answers for the pairs itself, so the look says nothing.
        bool PairingStartedWhileLooking()
        {
            _log.Info("Pairing check (" + reason + "): a pairing started while it looked, so it says nothing.");
            if (reason == "start")
            {
                // The coordinator's start check waits for this look; the pairing answers for the pairs now, and the check runs
                // once it ends (DropPairRunFlag).
                _protectionCheckOwed = true;
                return false;
            }

            return true;
        }

        // The radio read ended the look: a cancel (closing, or a press that superseded the connect) shows nothing more, as the
        // look's other cancels do; a Pair AirPods started meanwhile answers for the pairs itself.
        bool RadioStopped()
        {
            if (ct.IsCancellationRequested)
            {
                return _lifetime.IsCancellationRequested;
            }

            return _elevatedRun == PairRun && !_closing ? PairingStartedWhileLooking() : true;
        }
    }

    private enum RadioSays
    {
        Nothing,
        Shown,

        // The radio could not be read (a read that threw, or ran past its budget): the PC's Bluetooth may be the cause, so the
        // look cannot tell, says nothing and keeps the readings, as a failed paired-list read does.
        Unsure,

        // Closing, the look's token cancelled, or a pairing started during the read: nothing more is shown.
        Stop,
    }

    // No pinned pair in the paired list, or no AirPods in the tree, is also what a PC whose Bluetooth is off, disabled or missing
    // shows. The radio is read first (its own budget is a second): a state of the PC's own Bluetooth shows the radio's line and
    // button instead, and nothing kept for the pinned pair is dropped.
    private async Task<RadioSays> RadioExplainsAsync(string reason, CardPlace place, PinLookCards cards, CancellationToken ct)
    {
        if (_pairServices?.Radio is not { } radio)
        {
            return RadioSays.Nothing;
        }

        RadioCheckResult result;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await radio.CheckAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RadioSays.Stop;
        }

        _log.Info("Pairing check (" + reason + "): radio " + result.State + " in " + watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        if (_closing || _elevatedRun == PairRun)
        {
            return RadioSays.Stop;
        }

        if (result.State == RadioCheckState.Unknown)
        {
            _log.Info("Pairing check (" + reason + "): the radio could not be read, so whether the pairs are gone cannot be told.");
            return RadioSays.Unsure;
        }

        if (!result.BlocksRepair || result.State == RadioCheckState.Unreachable)
        {
            return RadioSays.Nothing;
        }

        if (!await ShowRadioCardAsync(result, place))
        {
            cards.Held = true;
            _log.Info("Pairing check (" + reason + "): the radio's card was held back by Windows.");
        }

        return RadioSays.Shown;
    }

    // A card with one button, in the same place as the other cards of the tray.
    private void ShowCardWithButton(string title, string text, string label, Action onClick, CardPlace place, bool staysUp = false)
    {
        if (_closing || _closed)
        {
            return;
        }

        var content = new CardContent(title, text, new CardAction(label, onClick), staysUp);
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
        _log.Info("Radio check after a failed connect, " + result.State + " in " + watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        return result;
    }

    // The radio's line, and its button when it has one.
    private void ShowRadioCard(RadioCheckResult radio, CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        CardContent content = RadioCard(radio, place);
        if (place.ClickPoint is { } point)
        {
            _registry.Cards.Show(content, place.Anchor, point);
        }
        else
        {
            _registry.Cards.Show(content, place.Anchor);
        }
    }

    // The same card, completing with whether Windows put it on screen.
    private async Task<bool> ShowRadioCardAsync(RadioCheckResult radio, CardPlace place)
    {
        if (_closing || _closed)
        {
            return true;
        }

        return await _registry.Cards.ShowAsync(RadioCard(radio, place), place.Anchor, place.ClickPoint);
    }

    private CardContent RadioCard(RadioCheckResult radio, CardPlace place)
    {
        string line = radio.Line ?? "";
        return radio.Action switch
        {
            RadioAction.BluetoothSettings => new CardContent(TrayStatus.AppName, line, new CardAction(
                RadioLines.BluetoothSettingsButton, () => Launch("open bluetooth settings", () => OpenBluetoothSettingsAsync(place), place))),
            RadioAction.RecordTrace when TraceOffered => new CardContent(TrayStatus.AppName, line, new CardAction(RadioLines.RecordTraceButton, () => AskForTrace(place))),
            _ => new CardContent(TrayStatus.AppName, line),
        };
    }

    // "No AirPods are paired with this PC." and the button that starts Pair AirPods. Once per run unless forced, counted only once
    // it was put on screen: a card Windows held back is offered again at the next look.
    private async Task OfferPairAirPodsAsync(CardPlace place, PinLookCards cards, bool force = false)
    {
        if (_closing || _closed || (_noAirPodsOffered && !force) || _pairServices is null || _registry.SafeMode || _elevatedRun == PairRun)
        {
            return;
        }

        _noAirPodsOffered = true;
        _log.Info("No AirPods are paired with this PC, so Pair AirPods is offered.");
        // Stays up until it is used: it is the way forward after an install with nothing paired, often shown at sign-in.
        if (!await ShowStayingCardAsync(PairAirPodsText.NoAirPodsPaired, PairAirPodsText.Title, () => StartPairAirPods(place), place, cards))
        {
            _noAirPodsOffered = false;
        }
    }

    // Whether one look's card was held back by Windows: then that look answered for nothing. One per look, so two looks that
    // overlap (a new device group during the start look) do not clear each other's.
    private sealed class PinLookCards
    {
        public bool Held { get; set; }
    }

    // A card that stays up, completing with whether it was put on screen; one held back is logged and told to its look.
    private async Task<bool> ShowStayingCardAsync(string text, string label, Action onClick, CardPlace place, PinLookCards cards)
    {
        if (_closing || _closed)
        {
            return true;
        }

        bool shown = await _registry.Cards.ShowAsync(new CardContent(TrayStatus.AppName, text, new CardAction(label, onClick), StaysUp: true), place.Anchor, place.ClickPoint);
        if (!shown)
        {
            cards.Held = true;
            _log.Info("Pairing check: the card \"" + text + "\" was held back by Windows, so it is offered again at the next look.");
        }

        return shown;
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

        PairRunEnd end = PairRunEnd.Nothing;
        var setupSteps = new List<StepOutcome>();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? unexpected = null;
        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            end = await PairAndAdoptAsync(services, place);
            if (end.Early is null && end.Session is not null && end.RunSetUp && !_closing)
            {
                // The run is handed straight from the pairing to set-up, so nothing else of the tray's own (an update, a Fix
                // started here) can start in between. The Fix lock is let go when the pairing ends, so a Fix from Setup.exe or
                // the install script could start now, with its own two questions. The pin was made a moment ago, so set-up's own
                // look at whether it is still paired is not needed.
                _elevatedRun = SetupRun;
                UpdatePresentation(forceIcon: false);
                var setupWatch = System.Diagnostics.Stopwatch.StartNew();
                ControllerResult? setUpResult = await RunSetupClaimedAsync(place);
                // Exit chosen during set-up waited for it, and the status is not read while Earshot closes: set-up's own result
                // then says how it ended.
                BootBlockStatus? after = _closing ? null : await ReadBlockStatusQuietlyAsync();
                bool done = after is not null ? !TrayStatus.NeedsSetUp(after) : _closing && setUpResult is { IsSuccess: true };

                // A set-up that did not take carries the code of its first failing step (a declined prompt is ERROR_CANCELLED).
                StepOutcome? failing = done ? null : setUpResult?.Steps.FirstOrDefault(s => !s.Ok);
                setupSteps.Add(failing is { } f
                    ? new StepOutcome("setup", false, f.Code, f.CodeName, f.Step)
                    : new StepOutcome("setup", done, done ? 0 : NativeCodes.NotAttempted, done ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null));
                setupSteps.Add(Milliseconds("setup-ms", setupWatch.ElapsedMilliseconds));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run still leaves its one history entry, with the type and code of what stopped it, after the steps it had
            // already taken when the session had ended (a throw in set-up); the caller logs it, once the run has let go and the
            // check below has been asked for.
            StepOutcome stopped = StepOutcomes.FromHResult("unexpected", ex.HResult, ex.GetType().Name, ok: false);
            if (end.Session is { } session)
            {
                List<StepOutcome> taken = StepsAfterTheSession(end);
                taken.AddRange(setupSteps);
                taken.Add(stopped);
                _coordinator.WriteHistory(session.ToHistoryEntry(_time.GetUtcNow(), taken, _pairRunSteps));
            }
            else
            {
                _coordinator.WriteHistory(EndedEarly(stopped, ConnectHistoryOutcomes.Failed, runWatch.ElapsedMilliseconds, _pairRunSession?.StepsSoFar).Early!);
            }

            unexpected = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            end = PairRunEnd.Nothing;
        }
        finally
        {
            EndElevatedRun(PairRun);
            EndElevatedRun(SetupRun);
            _pairRunFlagRaised = false;
            DropPairRunFlag(services, keep: _recoveringPair);
        }

        // Windows may have paired before the run stopped on something unexpected: a new pair may be paired and not protected, and
        // the checks for that waited while the run went on, so the one against the gate's own device runs now. (A person who kept
        // the pinned pair has just been told the new one is not protected, so that card is not replaced by this one.)
        if (unexpected is not null)
        {
            _coordinator.CheckProtectionAfterTrayPairing("a pairing stopped on something unexpected");
            unexpected.Throw();
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

        List<StepOutcome> later = StepsAfterTheSession(end);
        later.AddRange(setupSteps);
        _coordinator.WriteHistory(end.Session.ToHistoryEntry(_time.GetUtcNow(), later, _pairRunSteps));
    }

    // The steps a run took before its session (Look for AirPods and the marker's write), for its history entry. One pairing at a
    // time, so one list; each run starts it again.
    private readonly List<StepOutcome> _pairRunSteps = [];

    // The session of the run under way, so a run that throws out of it still records the steps it had taken.
    private PairingSession? _pairRunSession;

    // Each step after the session's own, with its code and how long it took.
    private static List<StepOutcome> StepsAfterTheSession(PairRunEnd end)
    {
        var later = new List<StepOutcome>();
        if (end.Replace is { } replaceStep)
        {
            later.Add(replaceStep);
            later.Add(Milliseconds("replace-ms", end.ReplaceMs));
        }

        if (end.Adopted is { } adopted && end.Replace is not { Ok: false })
        {
            later.Add(end.Adoption switch
            {
                DeviceChoiceResult.Applied => new StepOutcome("adopt", true, 0, "S_OK", null),
                DeviceChoiceResult.AppliedGateNotRepinned => new StepOutcome("adopt", false, 1, "gate-not-repinned", null),
                _ when !adopted && end.AdoptRefusal is { } refused => new StepOutcome("adopt", false, refused.Code, refused.CodeName, refused.Step),
                _ => new StepOutcome("adopt", adopted, adopted ? 0 : NativeCodes.NotAttempted, adopted ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null),
            });
            later.Add(Milliseconds("adopt-ms", end.AdoptMs));
        }

        return later;
    }

    // The one history entry of a run that ended before the session gave one: the step it stopped at, with its code and the time.
    // A run that ended before its session's own entry: the steps it had taken (Look for AirPods, the marker, and those of a
    // session that was cut off), then the step it stopped at and how long the run had taken by then (or, given stepMs, how long
    // that step took).
    private PairRunEnd EndedEarly(StepOutcome stoppedAt, string outcome, long ms, IReadOnlyList<StepOutcome>? sessionSteps = null, long? stepMs = null) => new(null, null, false)
    {
        Early = new ConnectHistoryEntry(
            _time.GetUtcNow(), ConnectHistoryActions.Pair, "none", "custom-pairing",
            ConnectHistory.FromSteps([.. _pairRunSteps, .. sessionSteps ?? [], stoppedAt, Milliseconds(stoppedAt.Step + "-ms", stepMs ?? ms)]),
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

        // The gate's first failing step when it refused the new pair, for the adopt step's code.
        public StepOutcome? AdoptRefusal { get; init; }

        // The answer to "use these instead?", when it was asked, and how long it took.
        public StepOutcome? Replace { get; init; }

        public long ReplaceMs { get; init; }
    }

    private async Task<PairRunEnd> PairAndAdoptAsync(PairAirPodsServices services, CardPlace place)
    {
        _pairRunSteps.Clear();
        _pairRunSession = null;
        using FixRunLock? runLock = FixRunLock.TryAcquire(services.LockName, _log);
        if (runLock is null)
        {
            _log.Info("Pair AirPods: not started, the Fix lock is held (by a Fix connection or pairing run, or by another program holding its name).");
            ShowCard(TrayStatus.AppName, FixRunLock.LockHeldMessage, place);
            return EndedEarly(new StepOutcome("lock", false, ExitCodes.AlreadyRunning, "already-running", null), ConnectHistoryOutcomes.Failed, 0);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IFixRePairUi ui = services.UiFactory(cancel);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HashSet<Guid>? before = null;
        PairingSession? session = null;
        bool marked = false;

        // A marker an earlier run left is that run's: this run may write over it, but only a recovery that has looked at what it
        // left clears it.
        bool earlierMarker = services.Marker.Exists();
        try
        {
            session = new PairingSession(services.Api, services.Nodes, ui, services.Options, _log, services.Radio);
            _pairRunSession = session;
            RadioCheckResult? radio = await session.CheckRadioAsync(cancel.Token);
            if (radio is not { BlocksRepair: true })
            {
                // look-ms is the question's time alone, after the radio read.
                var question = System.Diagnostics.Stopwatch.StartNew();
                FixPromptAnswer answer = services.Prompter.Ask(PairAirPodsText.FirstQuestion, _lifetime.Token);

                // Exit closed the question: that is Earshot closing, not the person saying no.
                if (_closing || _lifetime.IsCancellationRequested)
                {
                    _log.Info("Pair AirPods: Earshot is closing, so the first question was closed and nothing was scanned.");
                    return EndedEarly(new StepOutcome("closing", false, NativeCodes.NotAttempted, "exit", null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds);
                }

                if (answer.Answer != FixAnswer.Ok)
                {
                    _log.Info("Pair AirPods: the person did not press Look for AirPods, so nothing was scanned.");
                    return EndedEarly(new StepOutcome("look", false, NativeCodes.NotAttempted, answer.Answer.ToString(), null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds, stepMs: question.ElapsedMilliseconds);
                }

                _pairRunSteps.Add(new StepOutcome("look", true, 0, answer.Answer.ToString(), null));
                _pairRunSteps.Add(Milliseconds("look-ms", question.ElapsedMilliseconds));

                // Earshot may have begun closing as the button was pressed: nothing is written or scanned then.
                _lifetime.Token.ThrowIfCancellationRequested();
            }

            // From here a pairing may form, so the coordinator's automatic blocks and checks wait for the run. Not while only the
            // first question is up: nothing is scanned or paired then, and the question can stay up for minutes.
            _pairRunFlagRaised = true;
            services.RunFlag.Active = true;

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
                _pairRunSteps.Add(written);
            }

            PairingSessionResult result = await session.RunAsync(excludeAlreadyListed: false, cancel.Token, radio);
            _log.Info("Pair AirPods: " + result.Outcome + ", " + result.Reason + ".");
            if (result.Outcome == PairOutcome.Unverified)
            {
                // Paired and not looked at: maybe AirPods. The marker stays, so the next start looks again, and the picker is
                // offered now; nothing is adopted without the person's choice.
                _log.Warn("Pair AirPods: the paired device could not be checked, so the recovery marker is kept and the device picker is offered.");
                OfferChooseLeftPair(PairingText.Unverified, place);
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

                // Earshot stopped waiting (its own time limit) or the call threw: Windows may still finish the pairing after this
                // look, with nodes that arrive later, so the marker stays and the next start looks again (and clears it when
                // nothing was left).
                if (result.Reason == PairFailureReason.TimedOut || result.PairStatus is { Threw: true })
                {
                    _log.Info("Pair AirPods: the pairing was not answered by Windows (" + result.Reason + "), so the recovery marker is kept for the next start.");
                }
                else
                {
                    ClearOwnMarker(marked, earlierMarker, "the pairing left no AirPods to adopt");
                }

                ShowPairResult(result, radio, place);
                return new PairRunEnd(result, null, false);
            }

            // Earshot always asks before it takes on a different pair: the pick and the Pair press chose what to pair, not that it
            // takes the place of the pair Earshot is set to use.
            var replaceWatch = System.Diagnostics.Stopwatch.StartNew();
            FixAnswer? replace = AskToReplace(services, found, _registry.Settings.Current, _lifetime.Token);
            long replaceMs = replaceWatch.ElapsedMilliseconds;
            if (replace is { } kept && kept != FixAnswer.Ok)
            {
                if (kept == FixAnswer.Cancel && !_closing)
                {
                    _log.Info("Pair AirPods: the person kept the AirPods Earshot uses, so the new pair is left paired and not used.");
                    _keptPinnedPairAt = _time.GetUtcNow();
                    ClearOwnMarker(marked, earlierMarker, "the person kept the AirPods Earshot uses");
                    ShowCard(TrayStatus.AppName, PairAirPodsText.KeptOtherPair, place);
                }
                else
                {
                    // Not answered (the question timed out, or Earshot is closing): nobody decided, so the new pair is offered and
                    // the marker stays for the next start.
                    _log.Warn("Pair AirPods: whether to use the new pair was not answered (" + (_closing ? "closing" : kept.ToString()) + "), so the marker is kept.");
                    OfferChooseLeftPair(PairAirPodsText.ReplaceNotAnswered, place);
                }

                return new PairRunEnd(result, false, false)
                {
                    // Earshot closed the question, which is not the person's answer.
                    Replace = _closing
                        ? new StepOutcome("closing", false, NativeCodes.NotAttempted, "exit", null)
                        : new StepOutcome("replace", false, NativeCodes.NotAttempted, kept.ToString(), null),
                    ReplaceMs = replaceMs,
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
                    ReplaceMs = replaceMs,
                };
            }

            bool adopted = adoption == DeviceChoiceResult.Applied;
            if (!adopted)
            {
                _log.Warn("Pair AirPods: the recovery marker is kept, so the next start offers these AirPods again.");
            }
            else if (earlierMarker)
            {
                // The adoption cleared the marker, but an earlier run's was there before this one wrote its own: that run may have
                // left a pair this one did not look at, so the marker is written again for the next start to look.
                StepOutcome rewritten = services.Marker.Write();
                _log.Write(rewritten.Ok ? LogLevel.Info : LogLevel.Warn, "Pair AirPods: an earlier pairing's marker is written again after the adoption: " + rewritten.CodeName + ".");
            }

            bool setUp = false;
            if (adopted)
            {
                BootBlockStatus? status = await ReadBlockStatusQuietlyAsync();
                setUp = TrayStatus.NeedsSetUp(status);
            }

            if (!adopted)
            {
                // A pair paired and not adopted is not protected until one is chosen, so the card stays up with the picker.
                OfferChooseLeftPair(PairAirPodsText.PairedNotAdopted, place);
            }
            else
            {
                ShowCard(TrayStatus.AppName, setUp ? PairingText.SetsItselfUp : PairAirPodsText.PairedAndUsing, place);
            }

            return new PairRunEnd(result, adopted, setUp)
            {
                AdoptMs = adoptMs,
                Adoption = adoption,
                AdoptRefusal = adopted ? null : _lastGateRefusal,
                Replace = replace is null ? null : new StepOutcome("replace", true, 0, FixAnswer.Ok.ToString(), null),
                ReplaceMs = replaceMs,
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The run still leaves its history entry. The marker stays, so the next start looks at whatever was paired.
            _log.Info("Pair AirPods: cancelled because Earshot is closing; the recovery marker is kept.");
            return EndedEarly(new StepOutcome("closing", false, NativeCodes.NotAttempted, "exit", null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds, session?.StepsSoFar);
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
            else if (session?.PairCallStarted == true)
            {
                // Windows was asked to pair, and a cancel does not stop a bond already forming: its nodes can arrive seconds
                // later, so one look now proves nothing. The marker stays and the next start looks again.
                _log.Info("Pair AirPods: cancelled after Windows was asked to pair, so the recovery marker is kept for the next start.");
                ShowCard(TrayStatus.AppName, PairingText.Cancelled, place);
            }
            else
            {
                ClearOwnMarker(marked, earlierMarker, "the pairing was cancelled and left no new AirPods");
                ShowCard(TrayStatus.AppName, PairingText.Cancelled, place);
            }

            return EndedEarly(new StepOutcome("cancelled", false, NativeCodes.NotAttempted, "by-person", null), ConnectHistoryOutcomes.Cancelled, watch.ElapsedMilliseconds, session?.StepsSoFar);
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
    // The same AirPods paired again may come back as a new device group, so the address counts as the same pair too.
    private static FixAnswer? AskToReplace(PairAirPodsServices services, AirPodsCandidate found, EarshotSettings settings, CancellationToken closeOn)
    {
        Guid pinned = settings.PinnedContainerId;
        if (!NodeMatch.IsValidTargetContainer(pinned) || pinned == found.ContainerId ||
            (settings.PinnedAddress.Length > 0 && string.Equals(settings.PinnedAddress, found.Address, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var question = new FixPrompt(PairAirPodsText.Title, PairAirPodsText.ReplaceHeading, PairAirPodsText.ReplaceBody, [], [], PairAirPodsText.ReplaceButton);
        return services.Prompter.Ask(question, closeOn).Answer;
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
