using System.Diagnostics;
using System.Globalization;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Interop;
using Earshot.Repair;

namespace Earshot.Pairing;

// The pairing session shared by Pair AirPods and Fix connection's re-pair: look for AirPods in pairing mode, let the person
// choose, pair the one chosen, and check that Windows now holds AirPods by Earshot's identity rule.
//
//   0. The radio check (when one is given). Any state that blocks a pairing ends the session with its line, before any scan.
//   1. The scan: Bluetooth Classic devices that are not paired, listed by Windows' device watcher. Classic only: the pairing of the
//      debug run was all BR/EDR traffic; the LE traffic was the proximity broadcast, which battery uses and pairing does not.
//   2. Candidates: major class Audio/Video and minor class Headphones or Wearable headset, present. Loudspeakers, car audio and
//      the rest are never offered. Several candidates are shown for the person to choose; one is shown too. Never a guess.
//   3. The pick is the person's "Pair" press. PairAsync is not called before it returns a device, so a bare ConfirmOnly request
//      from Windows is answered yes only for that one device the person has just chosen. A PIN kind is shown and asked.
//   4. PairAsync is given PairTimeout; past it the pairing is cancelled and recorded as cancelled-timeout.
//   5. The model check: AirPodsDiscovery by address and container, polled up to PostPairWait. A device that is not AirPods is
//      NOT unpaired: Earshot leaves it as it is, says so and adopts nothing (owner stand-in decision of 25 October 2026).
//
// Adoption (pin, forget the previous pair, set-up) belongs to the caller. The session never allows, enables, disables, blocks
// or unpairs anything. What only a live run proves: the scan itself, which minor class AirPods models send, which pairing kind
// Windows asks for, PairAsync from this process, and real radio states.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationcustompairing.pairasync
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothminorclass

internal enum PairOutcome
{
    Paired,
    Failed,
    Cancelled,
    NotAirPods,

    // Paired, and Earshot could not look at the device: the paired list did not hold it once, or its nodes were not in the tree
    // when the wait ended. It is not called AirPods or not AirPods.
    Unverified,
}

internal enum PairFailureReason
{
    None,
    RadioNotReady,
    NoDevice,
    Cancelled,
    PairFailed,
    TimedOut,
    NotAirPods,
    Unverified,
}

internal sealed record PairingSessionResult(
    PairOutcome Outcome,
    PairFailureReason Reason,
    bool Paired,
    AirPodsCandidate? Adoptable,
    RadioCheckResult? Radio,
    PairingCallResult? PairStatus,
    bool NameUnread,
    bool Unreachable,
    int CandidateCount,
    long ScanMs,
    long DurationMs,
    string? CardLine,
    IReadOnlyList<string> Lines,
    IReadOnlyList<StepOutcome> HistorySteps,
    IReadOnlyList<StepOutcome> Steps)
{
    // The one connect-history entry of this run. Steps the caller does afterwards (adopt, setup) are added as later. Nothing in
    // it names a device: kinds, codes and code names only.
    // before: steps the caller took ahead of the session (Pair AirPods' question and its marker); later: those after it.
    public ConnectHistoryEntry ToHistoryEntry(DateTimeOffset at, IEnumerable<StepOutcome>? later = null, IEnumerable<StepOutcome>? before = null)
    {
        IEnumerable<StepOutcome> all = (before ?? []).Concat(HistorySteps).Concat(later ?? []);
        string outcome = Outcome switch
        {
            PairOutcome.Paired => ConnectHistoryOutcomes.Paired,
            PairOutcome.Cancelled => ConnectHistoryOutcomes.Cancelled,
            PairOutcome.NotAirPods => ConnectHistoryOutcomes.NotAirPods,
            PairOutcome.Unverified => ConnectHistoryOutcomes.Unverified,
            _ => ConnectHistoryOutcomes.Failed,
        };
        // PcSide only for the one signal of the pairing debug run (an unread name, then a failed pairing); never DeviceSide.
        FailureClass failure = Unreachable ? FailureClass.PcSide : Outcome == PairOutcome.Failed ? FailureClass.Unknown : FailureClass.None;
        return new ConnectHistoryEntry(
            at,
            ConnectHistoryActions.Pair,
            "none",
            "custom-pairing",
            ConnectHistory.FromSteps(all),
            false,
            outcome,
            failure.ToString(),
            DurationMs);
    }
}

internal sealed class PairingSession
{
    private readonly IPairingApi _api;
    private readonly INodeReader _nodes;
    private readonly IFixRePairUi _ui;
    private readonly FixOptions _options;
    private readonly ILog _log;
    private readonly IRadioCheck? _radio;

    public PairingSession(IPairingApi api, INodeReader nodes, IFixRePairUi ui, FixOptions options, ILog log, IRadioCheck? radio = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _api = api;
        _nodes = nodes;
        _ui = ui;
        _options = options;
        _log = log;
        _radio = radio;
    }

    // The radio check alone, for a caller that must know before it does anything else (Fix checks before it removes a pair).
    // Null when this session was given no radio check.
    public async Task<RadioCheckResult?> CheckRadioAsync(CancellationToken ct) =>
        _radio is null ? null : await _radio.CheckAsync(ct).ConfigureAwait(false);

    // excludeAlreadyListed: true for Fix, whose removal happens before the scan so the removed pair still shows up and must not
    // be offered back; false for Pair AirPods, where the person starts the scan, so whatever is present is offered.
    // checkedRadio: the result of a CheckRadioAsync the caller already made, so the radio is not read twice.
    // The history steps of the last run so far, for a caller whose run was cut off (cancelled, closed or thrown out of) before
    // the result was returned. Read only once that run has ended.
    internal IReadOnlyList<StepOutcome> StepsSoFar => Volatile.Read(ref _stepsSoFar) is { } steps ? [.. steps] : [];

    private List<StepOutcome>? _stepsSoFar;

    // Set once the last run asked Windows to pair, so a caller whose run was cancelled knows Windows may still finish the bond.
    internal bool PairCallStarted => Volatile.Read(ref _pairCallStarted);

    private bool _pairCallStarted;

    public async Task<PairingSessionResult> RunAsync(bool excludeAlreadyListed, CancellationToken ct, RadioCheckResult? checkedRadio = null)
    {
        var watch = Stopwatch.StartNew();
        var lines = new List<string>();
        var history = new List<StepOutcome>();
        Volatile.Write(ref _stepsSoFar, history);
        Volatile.Write(ref _pairCallStarted, false);
        var steps = new List<StepOutcome>();

        RadioCheckResult? radio = checkedRadio ?? await CheckRadioAsync(ct).ConfigureAwait(false);
        if (radio is not null)
        {
            history.Add(radio.ToStep());
            history.Add(Ms("radio-check-ms", radio.ElapsedMs));
            steps.AddRange(radio.ReadCodes.Where(s => !s.Ok));
            if (radio.BlocksRepair)
            {
                lines.Add(radio.Line ?? "");
                return Finish(PairOutcome.Failed, PairFailureReason.RadioNotReady, false, null, null, false, false, 0, 0, radio.Line);
            }
        }

        using IUnpairedWatch scan = _api.Watch();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seen = new List<WatchEvent>();
        var baselineDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pump = PumpAsync(scan, seen, baselineDone, pumpCts.Token);
        var scanTime = Stopwatch.StartNew();

        try
        {
            HashSet<string> baseline = [];
            int mark = 0;
            if (excludeAlreadyListed)
            {
                using (var baselineWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // A real wait, not the injected one: the list Windows already holds is read for as long as it takes, up to this.
                    Task limit = Task.Delay(_options.BaselineWait, baselineWait.Token);
                    await Task.WhenAny(baselineDone.Task, limit).ConfigureAwait(false);
                    await baselineWait.CancelAsync().ConfigureAwait(false);
                }

                lock (seen)
                {
                    // Everything up to the end of the first listing is what Windows already knew. Events that came in after that
                    // point, even before this line ran, are not part of it.
                    int listed = seen.FindIndex(e => e.Kind == WatchEventKind.InitialListDone);
                    mark = listed >= 0 ? listed + 1 : seen.Count;
                    baseline = seen.Take(mark).Where(e => e.Device is not null).Select(e => e.Device!.Id).ToHashSet(StringComparer.Ordinal);
                }
            }

            _ui.ShowInstruction(PairingText.Instruction);

            List<UnpairedDevice> candidates = await WaitForCandidatesAsync(seen, mark, baseline, excludeAlreadyListed, pump, ct).ConfigureAwait(false);
            long scanMs = scanTime.ElapsedMilliseconds;
            history.Add(new StepOutcome("scan", candidates.Count > 0, candidates.Count, "candidates", null));
            history.Add(new StepOutcome("scan-ms", true, (int)Math.Min(scanMs, int.MaxValue), "ms", null));
            if (candidates.Count == 0 && Aborted(seen))
            {
                // DeviceWatcherStatus.Aborted is 5.
                history.Add(new StepOutcome("scan-aborted", false, 5, "Aborted", null));
                lines.Add("Windows stopped the scan for Bluetooth devices (DeviceWatcherStatus Aborted). Nothing was paired.");
                return Finish(PairOutcome.Failed, PairFailureReason.NoDevice, false, null, null, false, false, 0, scanMs, PairingText.ScanStopped);
            }

            if (candidates.Count == 0)
            {
                lines.Add("No new Bluetooth audio device appeared. Nothing was paired.");
                return Finish(PairOutcome.Failed, PairFailureReason.NoDevice, false, null, null, false, false, 0, scanMs, PairingText.NothingFound);
            }

            var pickTime = Stopwatch.StartNew();
            UnpairedDevice? chosen = await _ui.PickAsync(candidates).ConfigureAwait(false);
            // Earshot's own step: 0 when a device was chosen, 1 when none was. Its time is how long the person took.
            history.Add(new StepOutcome("pick", chosen is not null, chosen is null ? 1 : 0, chosen is null ? "none-chosen" : "chosen", null));
            history.Add(Ms("pick-ms", pickTime.ElapsedMilliseconds));
            // A pick closed because the run ended (Exit, or the window's Cancel) is that, not the person choosing nothing.
            ct.ThrowIfCancellationRequested();
            if (chosen is null)
            {
                lines.Add("No device was chosen. Nothing was paired.");
                return Finish(PairOutcome.Cancelled, PairFailureReason.Cancelled, false, null, null, false, false, candidates.Count, scanMs, PairingText.Cancelled);
            }

            bool nameUnread = string.IsNullOrWhiteSpace(chosen.Name);
            if (nameUnread)
            {
                history.Add(new StepOutcome("name-unread", false, 1, "name-unread", null));
            }

            // The scan has done its job; stop it before pairing starts.
            await pumpCts.CancelAsync().ConfigureAwait(false);
            var pairTime = Stopwatch.StartNew();
            Volatile.Write(ref _pairCallStarted, true);
            PairingCallResult pair = await PairWithTimeoutAsync(chosen.Id, ct).ConfigureAwait(false);
            long pairMs = pairTime.ElapsedMilliseconds;
            string shown = nameUnread ? PairingText.NoName : chosen.Name;
            lines.Add("Pair " + shown + ": " + pair.Name + " (" + pair.Code.ToString(CultureInfo.InvariantCulture) + ")" + (pair.Detail is null ? "." : ", " + pair.Detail + "."));
            history.Add(new StepOutcome("pair", pair.Success, pair.Code, pair.Name, null));
            history.Add(Ms("pair-ms", pairMs));
            _log.Info("pair: " + (pair.Name == CancelledTimeout ? "Earshot stopped waiting for Windows: " : "Windows answered ") + pair.Name + " (" + pair.Code.ToString(CultureInfo.InvariantCulture) + ").");
            if (!pair.Success)
            {
                // The one PC-side signal of the debug run: the name request is the page that timed out, so Windows listed the
                // AirPods with no name, and the pairing then failed. Only a status that does not say the device answered counts
                // (PairReasons.SaysNoAnswer). A failed pairing of a device whose name was read, a timeout of this build's own, a
                // pairing the person or a handler stopped, a device not ready to pair, a status that is the device's answer and a
                // code that is no status say nothing about the PC. The PC-side reading rests on one sitting (25 October).
                bool timedOut = pair.Name == CancelledTimeout;
                bool unreachable = nameUnread && !timedOut && PairReasons.SaysNoAnswer(pair);
                string line = unreachable ? RadioLines.Unreachable : timedOut ? PairingText.TimedOut : PairReasons.LineFor(pair);
                lines.Add(line);
                PairFailureReason reason = timedOut ? PairFailureReason.TimedOut : PairFailureReason.PairFailed;
                PairOutcome outcome = PairReasons.StatusOf(pair) is PairReasons.PairingCanceled or PairReasons.RejectedByHandler ? PairOutcome.Cancelled : PairOutcome.Failed;
                return Finish(outcome, reason, false, null, pair, nameUnread, unreachable, candidates.Count, scanMs, line);
            }

            PairedListResult now;
            using (var listWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                listWait.CancelAfter(_options.PairedListWait);
                try
                {
                    now = await _api.ListPairedAsync(listWait.Token).WaitAsync(listWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Earshot's own time limit, so the pair is one that could not be looked at.
                    now = new PairedListResult([], [StepOutcomes.EarshotTimeout("pair-list", "The paired list did not answer within " + _options.PairedListWait.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.")]);
                    _log.Warn("pair: the paired list did not answer in time after the pairing, so the new pair could not be looked at.");
                }
            }

            steps.AddRange(now.Steps);

            // A list that failed or timed out goes in the history too, so a model check that could not read the device says why.
            history.AddRange(now.Steps.Where(s => s.Step == "pair-list" && !s.Ok));
            PairedDevice? newPaired = chosen.Address12.Length == 12 && now.Devices.Count(d => d.Address12 == chosen.Address12) == 1
                ? now.Devices.Single(d => d.Address12 == chosen.Address12)
                : null;
            var checkTime = Stopwatch.StartNew();
            AirPodsCandidate? match = null;
            bool looked = false;
            if (newPaired is not null)
            {
                for (TimeSpan waited = TimeSpan.Zero; ; waited += TimeSpan.FromSeconds(1))
                {
                    AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
                    steps.AddRange(found.Steps.Where(s => !s.Ok));
                    // A removed pairing leaves its nodes behind, not present: only a set whose device node is here now is the new pair.
                    match = found.Candidates.FirstOrDefault(c => c.DevicePresent && c.Address == newPaired.Address12 && c.ContainerId == newPaired.ContainerId);
                    looked = found.Listed && NodesArrived(newPaired.Address12, steps);
                    if (match is not null || waited >= _options.PostPairWait)
                    {
                        break;
                    }

                    await _options.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }

            // "Not AirPods" needs the device's own nodes to have been read and to lack the AirPods identity. A device the paired
            // list did not hold, or whose nodes were not in the tree by the end of the wait, could not be looked at, and saying it
            // is not AirPods would be a claim nobody checked.
            if (match is null && !looked)
            {
                history.Add(new StepOutcome("model-check", false, 2, "not-read", null));
                history.Add(Ms("model-check-ms", checkTime.ElapsedMilliseconds));
                lines.Add(shown + " is paired, but Earshot could not look at it to tell whether it is AirPods. Nothing was adopted.");
                _log.Warn("pair: the paired device could not be looked at (" + (newPaired is null ? "the paired list did not hold it once" : "its nodes were not in the tree") + "), so it was neither adopted nor called not AirPods.");
                return Finish(PairOutcome.Unverified, PairFailureReason.Unverified, true, null, pair, nameUnread, false, candidates.Count, scanMs, PairingText.Unverified);
            }

            history.Add(new StepOutcome("model-check", match is not null, match is null ? 1 : 0, match is null ? "not-airpods" : "airpods", null));
            history.Add(Ms("model-check-ms", checkTime.ElapsedMilliseconds));
            if (match is null)
            {
                // Not removed again: Earshot paired it in this run, but removing a pairing is the person's call (owner stand-in
                // decision of 25 October 2026). The result says it is still paired and adopts nothing.
                lines.Add(shown + " is paired but is not AirPods by Earshot's rule. It stays paired. Nothing was adopted.");
                _log.Info("pair: the paired device is not AirPods by the identity rule; it was left paired and nothing was adopted.");
                return Finish(PairOutcome.NotAirPods, PairFailureReason.NotAirPods, true, null, pair, nameUnread, false, candidates.Count, scanMs, PairingText.NotAirPods);
            }

            lines.Add(shown + " is paired and is AirPods by Earshot's rule.");
            return Finish(PairOutcome.Paired, PairFailureReason.None, true, match, pair, nameUnread, false, candidates.Count, scanMs, PairingText.SetsItselfUp);
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            _ui.Close();
        }

        PairingSessionResult Finish(
            PairOutcome outcome, PairFailureReason reason, bool paired, AirPodsCandidate? adoptable, PairingCallResult? status,
            bool nameUnread, bool unreachable, int candidateCount, long scanMs, string? cardLine) =>
            new(outcome, reason, paired, adoptable, radio, status, nameUnread, unreachable, candidateCount, scanMs, watch.ElapsedMilliseconds, cardLine, lines, history, steps);
    }

    // A step that carries how long the one before it took: its code is the milliseconds, as scan-ms has always been.
    private static StepOutcome Ms(string step, long ms) => new(step, true, (int)Math.Min(ms, int.MaxValue), "ms", null);

    // Whether a node of the paired device is in the tree now: a node whose instance id carries its address, located without the
    // phantom flag. A read that fails is a step with its CONFIGRET and counts as not arrived.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew
    private bool NodesArrived(string address12, List<StepOutcome> steps)
    {
        uint cr = _nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return false;
        }

        foreach (string id in ids.Where(i => i.Contains(address12, StringComparison.OrdinalIgnoreCase)))
        {
            if (_nodes.Locate(id, includeNonPresent: false, out _) == CfgMgr32.CR_SUCCESS)
            {
                return true;
            }
        }

        return false;
    }

    // The name PairingCallResult carries for a pairing this build cancelled after PairTimeout. 1460 is ERROR_TIMEOUT.
    internal const string CancelledTimeout = "cancelled-timeout";

    private const int ErrorTimeout = 1460;

    private async Task<PairingCallResult> PairWithTimeoutAsync(string deviceId, CancellationToken ct)
    {
        using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pairCts.CancelAfter(_options.PairTimeout);
        try
        {
            // WaitAsync ends the wait at the limit whether or not the operation honours its token.
            return await _api.PairAsync(deviceId, new Prompt(_ui, pairCts.Token), pairCts.Token).WaitAsync(pairCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("pair: the pairing was cancelled after " + _options.PairTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s (Earshot's own time limit, cancelled-timeout).");
            return new PairingCallResult(CancelledTimeout, ErrorTimeout, false, null);
        }
    }

    // Reads the watcher into the list until it ends or the scan is cancelled. The end of a scan by cancellation is how the scan
    // is meant to stop, and is recorded in the log; any other failure is not swallowed.
    private async Task PumpAsync(IUnpairedWatch watch, List<WatchEvent> seen, TaskCompletionSource baselineDone, CancellationToken ct)
    {
        try
        {
            await foreach (WatchEvent e in watch.EventsAsync(ct).ConfigureAwait(false))
            {
                lock (seen)
                {
                    seen.Add(e);
                }

                if (e.Kind == WatchEventKind.InitialListDone)
                {
                    baselineDone.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info("pair: the scan for unpaired devices was stopped.");
        }
        finally
        {
            baselineDone.TrySetResult();
        }
    }

    private async Task<List<UnpairedDevice>> WaitForCandidatesAsync(
        List<WatchEvent> seen, int mark, HashSet<string> baseline, bool exclude, Task pump, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ScanTimeout);
        bool settling = false;
        while (true)
        {
            List<UnpairedDevice> found = CandidatesNow(seen, mark, baseline, exclude);
            if (found.Count > 0 && !settling)
            {
                settling = true;
                timeout.CancelAfter(_options.SettleWait);
            }

            if (pump.IsCompleted || timeout.IsCancellationRequested || Aborted(seen))
            {
                // A Cancel in the window ends the scan as a cancel: no pick is shown and "nothing found" is not said.
                ct.ThrowIfCancellationRequested();

                // One last look: events that arrived while the wait ended.
                return CandidatesNow(seen, mark, baseline, exclude);
            }

            try
            {
                await Task.WhenAny(pump, _options.Delay(TimeSpan.FromMilliseconds(10), timeout.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool Aborted(List<WatchEvent> seen)
    {
        lock (seen)
        {
            return seen.Exists(e => e.Kind == WatchEventKind.Aborted);
        }
    }

    // Bluetooth minor classes of the Audio/Video major class that AirPods report: Wearable headset device (1) and Headphones (6).
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothminorclass
    internal const uint MinorWearableHeadset = 1;
    internal const uint MinorHeadphones = 6;

    internal static bool IsCandidate(UnpairedDevice d) =>
        d.IsAudioVideo && d.MinorClass is MinorWearableHeadset or MinorHeadphones && d.IsPresent != false;

    // The devices the scan lists now: each id's events merged into one device (a later event only fills in what it carries), a
    // removed one dropped, and only those that pass the candidate rule. With exclude, a device already in the list before the
    // person's instruction is never a candidate.
    internal static List<UnpairedDevice> CandidatesNow(List<WatchEvent> seen, int mark, HashSet<string> baseline, bool exclude)
    {
        var merged = new Dictionary<string, UnpairedDevice>(StringComparer.Ordinal);
        var order = new List<string>();
        lock (seen)
        {
            foreach (WatchEvent e in seen.Skip(mark))
            {
                if (e.Device is null)
                {
                    continue;
                }

                string id = e.Device.Id;
                if (e.Kind == WatchEventKind.Removed)
                {
                    merged.Remove(id);
                    order.Remove(id);
                    continue;
                }

                if (e.Kind is not (WatchEventKind.Added or WatchEventKind.Updated) || (exclude && baseline.Contains(id)))
                {
                    continue;
                }

                if (merged.TryGetValue(id, out UnpairedDevice? current))
                {
                    merged[id] = Merge(current, e.Device);
                }
                else
                {
                    merged[id] = e.Device;
                    order.Add(id);
                }
            }
        }

        return order.Select(id => merged[id]).Where(IsCandidate).ToList();
    }

    private static UnpairedDevice Merge(UnpairedDevice current, UnpairedDevice later) => current with
    {
        Name = string.IsNullOrWhiteSpace(later.Name) ? current.Name : later.Name,
        Address12 = later.Address12.Length == 0 ? current.Address12 : later.Address12,
        IsAudioVideo = current.IsAudioVideo || later.IsAudioVideo,
        MinorClass = later.MinorClass ?? current.MinorClass,
        IsPresent = later.IsPresent ?? current.IsPresent,
    };

    // The pairing request is answered after the person has pressed "Pair" on this one device, which is what a bare confirmation
    // asks. A number to compare or to enter is shown and asked.
    // The attempt's token goes with the number, so the question closes when the attempt ends.
    private sealed class Prompt(IFixRePairUi ui, CancellationToken attempt) : IPairingPrompt
    {
        public Task<bool> ConfirmAsync(FixPairKind kind, string? pin) => kind switch
        {
            FixPairKind.ConfirmOnly => Task.FromResult(true),
            FixPairKind.ConfirmPinMatch or FixPairKind.DisplayPin => ui.ConfirmPinAsync(kind, pin, attempt),
            _ => Task.FromResult(false),
        };
    }
}
