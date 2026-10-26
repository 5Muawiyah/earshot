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
            Launch("recover a pairing", () => RecoverPairingAsync(services, CardPlace.NearTray), CardPlace.NearTray);
            return;
        }

        Launch("check the pinned pair", () => CheckPinnedPairAsync(reason, CardPlace.NearTray), CardPlace.NearTray);
    }

    // A marker from a run that paired and did not adopt. The stale-pin check comes first and says what it found; then a pair of
    // AirPods that is paired and not the pinned one is offered through the device picker, never adopted by itself. The marker
    // stays until a pair is chosen, or until no such pair is left.
    private async Task RecoverPairingAsync(PairAirPodsServices services, CardPlace place)
    {
        _log.Warn("Pair AirPods: an earlier pairing ended before its AirPods were adopted, so they may be paired, unpinned and unblocked.");
        if (!await CheckPinnedPairAsync("pairing recovery", place) || _closing)
        {
            return;
        }

        AirPodsDiscoveryResult found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), _lifetime.Token);
        if (!found.Listed)
        {
            _log.Warn("Pair AirPods: the devices could not be listed, so the pairing that did not finish is looked at again next start.");
            return;
        }

        Guid pinned = _registry.Settings.Current.PinnedContainerId;
        int left = found.Candidates.Count(c => c.DevicePresent && c.ContainerId != pinned);
        if (left == 0)
        {
            ClearPairMarker("no paired AirPods other than the pinned ones are left");
            return;
        }

        _log.Info("Pair AirPods: " + left.ToString(System.Globalization.CultureInfo.InvariantCulture) + " paired set(s) of AirPods are not the pinned ones, so the device picker is offered.");
        ShowCardWithButton(
            TrayStatus.AppName, PairAirPodsText.RecoverChoose, PairAirPodsText.ChooseButton,
            () => Launch("choose device", () => ChooseDeviceAsync(place), place), place);
    }

    private void ClearPairMarker(string why)
    {
        if (_pairServices is not { } services || !services.Marker.Exists())
        {
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
                AirPodsDiscoveryResult found = await Task.Run(() => AirPodsDiscovery.Find(services.Nodes), _lifetime.Token);
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
        if (_closing || _closed || (_noAirPodsOffered && !force) || _pairServices is null || _registry.SafeMode)
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
        try
        {
            end = await PairAndAdoptAsync(services, place);
        }
        finally
        {
            EndElevatedRun(PairRun);
        }

        if (end.Session is null)
        {
            return;
        }

        var later = new List<StepOutcome>();
        if (end.Adopted is { } adopted)
        {
            later.Add(new StepOutcome("adopt", adopted, adopted ? 0 : NativeCodes.NotAttempted, adopted ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null));
        }

        if (end.RunSetUp && !_closing)
        {
            await RunSetupAsync(place);
            BootBlockStatus? after = await ReadBlockStatusQuietlyAsync();
            bool done = after is not null && !TrayStatus.NeedsSetUp(after);
            later.Add(new StepOutcome("setup", done, done ? 0 : NativeCodes.NotAttempted, done ? "S_OK" : NativeCodes.Name(NativeCodes.NotAttempted), null));
        }

        _coordinator.WriteHistory(end.Session.ToHistoryEntry(_time.GetUtcNow(), later));
    }

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
    }

    private async Task<PairRunEnd> PairAndAdoptAsync(PairAirPodsServices services, CardPlace place)
    {
        using FixRunLock? runLock = FixRunLock.TryAcquire(services.LockName, _log);
        if (runLock is null)
        {
            _log.Info("Pair AirPods: not started, a Fix connection or a pairing is already running.");
            ShowCard(TrayStatus.AppName, FixRunLock.PairAlreadyRunningMessage, place);
            return PairRunEnd.Nothing;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IFixRePairUi ui = services.UiFactory(cancel);
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
                    return PairRunEnd.Nothing;
                }
            }

            // Written before anything can be paired, so the time between the pairing and the adoption is always covered.
            // Without it a crash between the pairing and the adoption would go unnoticed, so no pairing starts when it cannot be written.
            StepOutcome marked = services.Marker.Write();
            _log.Write(marked.Ok ? LogLevel.Info : LogLevel.Warn, "Pair AirPods: recovery marker written: " + marked.CodeName + " " + marked.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            if (!marked.Ok)
            {
                ShowCard(TrayStatus.AppName, PairAirPodsText.MarkerNotWritten(marked.Code), place);
                return PairRunEnd.Nothing;
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
                ClearPairMarker("the pairing left no AirPods to adopt");
                ShowPairResult(result, radio, place);
                return new PairRunEnd(result, null, false);
            }

            DeviceChoiceResult adoption = await AdoptPairedAirPodsAsync(found, place);
            if (adoption == DeviceChoiceResult.AppliedGateNotRepinned)
            {
                // Pinned in the tray only: the gate still names the previous pair, so the new one is not blocked at rest. Its own card
                // says so, and the marker stays for the next start's check.
                _log.Warn("Pair AirPods: the boot block status could not be read, so the gate was not given the new pair; the recovery marker is kept.");
                return new PairRunEnd(result, false, false);
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
            return new PairRunEnd(result, adopted, setUp);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log.Info("Pair AirPods: cancelled because Earshot is closing.");
            return PairRunEnd.Nothing;
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

    // The pair the person has just paired is the one they chose, so it is adopted at once: pinned, given to the gate when
    // Earshot is set up, and the previous pair's readings and link dropped.
    private async Task<DeviceChoiceResult> AdoptPairedAirPodsAsync(AirPodsCandidate found, CardPlace place)
    {
        string name = string.IsNullOrWhiteSpace(found.Name) ? "AirPods" : found.Name;
        DeviceChoiceResult applied = await ApplyDeviceChoiceAsync(new PickerChoice(name, name, found.Address, found.ContainerId), place);
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
