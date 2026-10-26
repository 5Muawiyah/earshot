using System.Globalization;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Diagnostics;

namespace Earshot.Repair;

// Fix connection, as the unelevated program runs it.
//
//   1. Plan, from reads only: the AirPods the identity rule finds, Earshot's records, what Earshot's own node choice picks.
//   2. Print the plan. With nobody at the screen (no input desktop, a different desktop, not interactive) stop here: nothing
//      is asked, elevated or changed, and the exit code says "plan only".
//   3. First question (an on-screen dialog): every change. On OK, one administrator prompt runs `fix-apply`, which builds the
//      plan again itself and applies the part both agree on. With nothing to undo it is still asked, as a question of its own
//      (owner stand-in decision of 25 October 2026: two explicit on-screen questions, however empty the plan).
//   4. Second question (a separate dialog): remove the AirPods from Windows and pair them again, by name. On OK, in this
//      session: the radio check, then unpair, scan, show the instruction, pick, pair, check the new device with the identity
//      rule, adopt it through one gate run when Earshot is installed. The person's "Pair" press on the chosen device is a
//      step of the pairing and is not one of the two questions.
//   5. Every path that changed something and ends without an adoption ends with the normal block when Earshot is installed and
//      Block at boot is on. A marker file covers a crash in between: the tray answers a stale one with the block at its next
//      start (FixRecovery).
//   6. A result: every change, every status and code, and what could not be done.
//
// Consent comes from the dialogs and from nothing else. Arguments are refused rather than read: there is no option that says
// yes.
internal enum FixOutcome
{
    Usage,
    PlanOnly,
    Declined,
    DeclinedAfterChanges,
    Done,
    Failed,
}

internal sealed record FixResult(FixOutcome Outcome, IReadOnlyList<string> Lines, IReadOnlyList<StepOutcome> Steps)
{
    // The re-pair found the AirPods with no name read and the pairing then did not finish (Unreachable): the one result Earshot
    // reads as this PC's Bluetooth, where a trace can show where it stopped, as the tray's card for the same line offers.
    public bool OfferTrace { get; init; }

    public int ExitCode => Outcome switch
    {
        FixOutcome.Usage => Earshot.ExitCodes.Usage,
        FixOutcome.PlanOnly => Earshot.ExitCodes.PlanOnly,
        FixOutcome.Declined => Earshot.ExitCodes.Declined,
        FixOutcome.DeclinedAfterChanges => Earshot.ExitCodes.DeclinedAfterChanges,
        FixOutcome.Failed => Earshot.ExitCodes.Software,
        _ => Earshot.ExitCodes.Ok,
    };
}

internal static class FixConnection
{
    public const string Usage = "Usage: Earshot.exe fix [--out <path>]. Earshot asks on the screen before it changes anything, so it takes no yes, force or consent option.";

    public const string NoProtectedFolderNote = "Fix connection could not make a folder that only administrators can reach for its plan, so nothing was changed.";

    public const string PlanOnlyNote = "Nothing was changed. Earshot asks on the screen before it changes anything, so run Fix connection where a person can answer.";

    public const string SetUpFirstNote = "Set up or update Earshot first. Fix connection makes its changes through the installed copy, so nothing was changed.";

    public const string RecordTraceButton = "Record a Bluetooth trace";

    public const string TraceNotHeardNote = "Earshot didn't answer, so the trace can't be started from here. Start Earshot, then choose Bluetooth trace in its settings.";

    // The only option: where the plan is printed. It is read by the caller that opens the output; here it is accepted and ignored.
    internal static bool TryParseArgs(IReadOnlyList<string> args, out string problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        problem = "";
        for (int i = 0; i < args.Count; i++)
        {
            if (i == 0 && args[i] == "fix")
            {
                continue;
            }

            if (args[i] == "--out" && i + 1 < args.Count && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
            {
                i++;
                continue;
            }

            problem = "Unknown option '" + new string(args[i].Where(c => c is >= ' ' and <= '~').Take(32).ToArray()) + "'. " + Usage;
            return false;
        }

        return true;
    }

    public static async Task<FixResult> RunAsync(IReadOnlyList<string> args, FixHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        if (!TryParseArgs(args, out string problem))
        {
            host.Output.WriteLine(problem);
            return new FixResult(FixOutcome.Usage, [problem], []);
        }

        var steps = new List<StepOutcome>();
        FixPlanResult planned = FixPlanner.Build(host.Nodes, host.Records, host.Leftovers);
        steps.AddRange(planned.Steps);
        FixPlan plan = planned.Plan;
        host.Output.WriteLine(FixPlanText.Describe(plan));

        var dialog = new FixConsentDialog(host.Prompter, host.Desktop, host.Log);
        if (!dialog.CanAsk())
        {
            host.Output.WriteLine(PlanOnlyNote);
            return new FixResult(FixOutcome.PlanOnly, [PlanOnlyNote], steps);
        }

        FixResult result = await RunWithPersonAsync(dialog, plan, host, steps, ct).ConfigureAwait(false);
        host.Output.WriteLine(string.Join(Environment.NewLine, result.Lines));
        bool offerTrace = result.OfferTrace && host.RequestTrace is not null;
        if (host.RePairUi.ShowResult(string.Join(Environment.NewLine, result.Lines), offerTrace) && offerTrace)
        {
            // The tray shows the note and its Record button; nothing is recorded from here.
            host.Log.Info("fix: the person asked for a Bluetooth trace from the result.");
            if (!host.RequestTrace!())
            {
                host.RePairUi.ShowResult(TraceNotHeardNote, offerTrace: false);
            }
        }

        return result;
    }

    // What a run has done so far, for the closing block to read.
    private sealed class RunState
    {
        public bool Touched { get; set; }

        // The first question's administrator run ended with exit code 0, so the changes it listed were made.
        public bool FirstRunSucceeded { get; set; }

        public bool Adopted { get; set; }

        // A new pair was paired and offered for adoption, and the adoption has not succeeded. The closing block can only reach
        // the pin Earshot still holds, the pair that was removed, so it must not be reported as protecting anything.
        public bool NewPairUnprotected { get; set; }

        public bool Installed { get; init; }

        // The re-pair's pairing was Unreachable: found with no name read, then not finished.
        public bool PcBluetoothUnreachable { get; set; }
    }

    // The first question when there is nothing to undo. It issues no consent and starts nothing: the second question, which
    // names what will be removed, is asked next, and nothing is removed before it is answered.
    private static readonly FixPrompt NothingToUndoPrompt = new(
        FixPromptText.Title,
        "Nothing of Earshot's to undo",
        "Earshot has no change of its own to undo. The next question asks whether to remove your AirPods from Windows and pair them again. Nothing is removed until you answer it.",
        [],
        [],
        "Go on");

    private static async Task<FixResult> RunWithPersonAsync(FixConsentDialog dialog, FixPlan plan, FixHost host, List<StepOutcome> steps, CancellationToken ct)
    {
        var lines = new List<string>();
        var state = new RunState { Installed = FixRules.ShouldEndWithBlock(host.Records.ReadDevice(), host.Records.ReadConfig()) };
        FixOutcome outcome;

        try
        {
            outcome = await ChangeAndRePairAsync(dialog, plan, host, state, lines, steps, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lines.Add("Cancelled.");
            outcome = FixOutcome.Failed;
        }
        catch (Exception ex)
        {
            // Surfaced, not swallowed: the type and the raw HRESULT are in the result, and the block below still runs.
            lines.Add("Stopped by " + ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
            host.Log.Error("fix: stopped by " + ex.GetType().Name, ex);
            outcome = FixOutcome.Failed;
        }

        bool ended = await EndAsync(host, state, lines, steps).ConfigureAwait(false);
        return new FixResult(ended ? outcome : FixOutcome.Failed, lines, steps) { OfferTrace = state.PcBluetoothUnreachable };
    }

    private static async Task<FixOutcome> ChangeAndRePairAsync(
        FixConsentDialog dialog, FixPlan plan, FixHost host, RunState state, List<string> lines, List<StepOutcome> steps, CancellationToken ct)
    {
        FixPlan work = plan;
        if (plan.IsEmpty)
        {
            // Nothing to undo, and still a first question: the person agrees to go on before the second one is shown.
            if (host.Prompter.Ask(NothingToUndoPrompt, ct).Answer != FixAnswer.Ok)
            {
                lines.Add("Declined. Nothing was changed.");
                return FixOutcome.Declined;
            }
        }
        else
        {
            // Changes need the administrator run, and only the installed copy is raised: with none, nothing is asked.
            if (host.Elevator is NotInstalledFixElevator)
            {
                steps.Add(NotInstalledFixElevator.Why);
                host.Log.Warn("fix: Earshot is not installed, so the changes were not offered.");
                lines.Add(SetUpFirstNote);
                return FixOutcome.Failed;
            }

            FixConsentAnswer first = dialog.AskChanges(plan);
            if (first.Consent is null)
            {
                lines.Add("Declined. Nothing was changed.");
                return FixOutcome.Declined;
            }

            if (plan.NeedsPick)
            {
                FixDevice picked = plan.Devices.Where(d => d.Nodes.Count > 0).ElementAt(first.Chosen);
                work = plan.OnlyDevice(picked.Address);
            }

            FixPlanFileInfo file = host.PlanFiles.Write(work);
            steps.Add(host.Recovery.Begin("restoring"));
            ElevatedRun run;

            // Counted as touched before the wait: the administrator run may change the nodes and then the wait end in an
            // exception, and the closing block must still follow. Only a run Windows never started clears it again.
            state.Touched = true;
            try
            {
                run = await host.Elevator.ApplyAsync(first.Consent, file.Path, file.Sha256, ct).ConfigureAwait(false);
            }
            finally
            {
                Record(host, steps, host.PlanFiles.Delete(file));
            }

            steps.Add(run.Step);
            if (run.ExitCode is not int exit)
            {
                state.Touched = false;
                lines.Add("Windows did not start the administrator run: " + run.Step.CodeName + " (" + run.Step.Code.ToString(CultureInfo.InvariantCulture) + "). Nothing was changed.");
                return FixOutcome.Failed;
            }

            state.Touched = true;
            state.FirstRunSucceeded = exit == 0;
            string exitName = GateExitCodes.NameOf(exit) ?? "unknown";
            lines.Add("The administrator run ended: " + exitName + " (" + exit.ToString(CultureInfo.InvariantCulture) + ").");
            AddChangeLines(lines, host, file.Sha256);
            FixPlanResult after = FixPlanner.Build(host.Nodes, host.Records, host.Leftovers);
            int remaining = after.Plan.Devices.SelectMany(d => d.Nodes).Count(n => work.Devices.SelectMany(w => w.Nodes).Any(w => string.Equals(w.InstanceId, n.InstanceId, StringComparison.OrdinalIgnoreCase)));
            if (remaining > 0)
            {
                lines.Add("Could not be undone: " + remaining.ToString(CultureInfo.InvariantCulture) + " part(s) are still as Earshot left them.");
            }
        }

        if (plan.IsEmpty)
        {
            lines.Add("Earshot has no change to undo.");
        }

        // The device to remove and pair again: the one the records pin, or the only one, and never a guess among several.
        FixDevice? target = work.Devices.FirstOrDefault(d => d.Pinned) ?? (work.Devices.Count == 1 ? work.Devices[0] : null);
        if (target is null && work.Devices.Count > 1)
        {
            lines.Add("More than one pair of AirPods is on this PC and none is the one Earshot protects, so none is removed.");
        }

        FixConsent? second = dialog.AskRePair(target is null ? [] : [target.Name], removeFirst: target is not null);
        if (second is null)
        {
            if (state.Touched && state.FirstRunSucceeded)
            {
                lines.Add("Declined. The changes you agreed to first were made. The AirPods were not removed or paired again.");
                return FixOutcome.DeclinedAfterChanges;
            }

            if (state.Touched)
            {
                // The first step's run did not end well, so some of its changes may not have been made: not a declined fix but a
                // failed one, whose result lines above say how far it got.
                lines.Add("Declined. The first step did not finish, so its changes may be only partly made. The AirPods were not removed or paired again.");
                return FixOutcome.Failed;
            }

            lines.Add("Declined. The AirPods were left as they are.");
            return FixOutcome.Declined;
        }

        steps.Add(host.Recovery.Begin("repairing"));
        bool touchedBefore = state.Touched;
        state.Touched = true;
        var repairer = new RePairer(host.Pairing, host.Nodes, host.RePairUi, host.Options, host.Log, host.Radio);
        var pairWatch = System.Diagnostics.Stopwatch.StartNew();
        RePairResult repaired;
        try
        {
            repaired = await repairer.RunAsync(second, target is null ? null : new RePairTarget(target.Address, target.Name), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (repairer.Began)
        {
            // Cut off by the person's Cancel or the window's close, or by a fault, after the pair was removed or the scan began:
            // the run still leaves its one entry, and the result and the closing block follow as before.
            RecordCutOff(host, steps, [.. repairer.StepsBeforeTheSession, .. repairer.SessionStepsSoFar], ex, ex is OperationCanceledException && ct.IsCancellationRequested, pairWatch.ElapsedMilliseconds);
            throw;
        }

        if (!repaired.Touched)
        {
            // Refused before anything was removed or scanned (the radio check): nothing here needs the closing block.
            state.Touched = touchedBefore;
        }

        lines.AddRange(repaired.Lines);
        steps.AddRange(repaired.Steps);
        state.PcBluetoothUnreachable = repaired.Session?.Unreachable == true;
        foreach (PairingCallResult status in repaired.Statuses)
        {
            lines.Add("Status: " + status.Name + " (" + status.Code.ToString(CultureInfo.InvariantCulture) + ").");
        }

        StepOutcome? adoptStep = null;
        long? adoptMs = null;
        if (repaired.Adoptable is { } candidate)
        {
            if (!state.Installed)
            {
                lines.Add("Earshot is not installed (or Block at boot is off), so nothing was adopted.");
                adoptStep = new StepOutcome("adopt", true, 0, "not-set-up", null);
            }
            else
            {
                state.NewPairUnprotected = true;
                var adoptWatch = System.Diagnostics.Stopwatch.StartNew();
                FixGateResult adoption = await host.Gate.AdoptDeviceAsync(candidate.Address, ct).ConfigureAwait(false);
                adoptMs = adoptWatch.ElapsedMilliseconds;
                steps.AddRange(adoption.Steps);
                state.Adopted = adoption.Ok;
                state.NewPairUnprotected = !adoption.Ok;
                adoptStep = new StepOutcome("adopt", adoption.Ok, adoption.ExitCode ?? 0, adoption.Ok ? "adopted" : "not-adopted", null);
                lines.Add(adoption.Ok
                    ? "Earshot now protects the new pair, and it is blocked at rest."
                    : "Could not adopt the new pair: " + adoption.Summary + (adoption.ExitCode is int code ? " (" + code.ToString(CultureInfo.InvariantCulture) + ")" : "") + ".");
            }
        }

        RecordPairing(host, steps, repaired, repairer, adoptStep, adoptMs, pairWatch.ElapsedMilliseconds);

        return repaired.Paired && repaired.Failure == RePairFailure.None && (state.Adopted || !state.Installed) ? FixOutcome.Done : FixOutcome.Failed;
    }

    // The one connect-history entry of this run's pairing, when the run has a history to write to and a pairing session ran (or
    // was refused by the radio check), or the old pair's removal was tried: the removal's steps lead it, with their codes and
    // time. Nothing in it names a device. A failed write is a step with its raw code.
    private static void RecordPairing(FixHost host, List<StepOutcome> steps, RePairResult repaired, RePairer repairer, StepOutcome? adoptStep, long? adoptMs, long runMs)
    {
        if (host.History is null || (repaired.Session is null && !repairer.Began))
        {
            return;
        }

        List<StepOutcome>? later = adoptStep is null ? null : [adoptStep];
        if (later is not null && adoptMs is long ms)
        {
            later.Add(new StepOutcome("adopt-ms", true, (int)Math.Min(ms, int.MaxValue), "ms", null));
        }

        // A removal that failed or was not answered ends the run before any session: its entry is the removal alone.
        ConnectHistoryEntry entry = repaired.Session is { } session
            ? session.ToHistoryEntry(host.Time.GetUtcNow(), later, repairer.StepsBeforeTheSession)
            : new ConnectHistoryEntry(
                host.Time.GetUtcNow(), ConnectHistoryActions.Pair, "none", "custom-pairing",
                ConnectHistory.FromSteps(repairer.StepsBeforeTheSession), false, ConnectHistoryOutcomes.Failed, FailureClass.None.ToString(), runMs);
        steps.Add(host.History.Append(entry));
    }

    // The entry of a pairing that was cut off: the session's steps so far, then the step it stopped at with its code and the time.
    private static void RecordCutOff(FixHost host, List<StepOutcome> steps, IReadOnlyList<StepOutcome> sessionSteps, Exception ex, bool cancelled, long ms)
    {
        if (host.History is null)
        {
            return;
        }

        StepOutcome stopped = cancelled
            ? new StepOutcome("cancelled", false, NativeCodes.NotAttempted, "by-person", null)
            : StepOutcomes.FromHResult("unexpected", ex.HResult, ex.GetType().Name, ok: false);
        var entry = new ConnectHistoryEntry(
            host.Time.GetUtcNow(), ConnectHistoryActions.Pair, "none", "custom-pairing",
            ConnectHistory.FromSteps([.. sessionSteps, stopped, new StepOutcome(stopped.Step + "-ms", true, (int)Math.Min(ms, int.MaxValue), "ms", null)]),
            false, cancelled ? ConnectHistoryOutcomes.Cancelled : ConnectHistoryOutcomes.Failed, FailureClass.None.ToString(), ms);
        steps.Add(host.History.Append(entry));
    }

    // The block that ends every path that changed something. An adoption is a pin and a block in one gate run, so it needs no
    // second one. The block is sent with no cancellation: a cancelled run still ends protected. False when the block was
    // needed and did not happen, which leaves the marker for the tray's next start.
    internal const string NewPairUnprotectedLine = "The new pair is not protected yet. Run Fix connection again.";

    // The marker says a run is under way. One that stays makes the tray's next start block once more, which is safe, so it is
    // said and logged rather than treated as a failure.
    private static void ClearMarker(FixHost host, List<string> lines, List<StepOutcome> steps)
    {
        StepOutcome cleared = host.Recovery.Clear();
        if (Record(host, steps, cleared))
        {
            return;
        }

        lines.Add("The record of this run could not be removed (code 0x" + cleared.Code.ToString("X8", CultureInfo.InvariantCulture) +
                  "). Earshot blocks the AirPods once more the next time it starts.");
    }

    // Adds the step; a failed one also goes to the log with its raw code.
    private static bool Record(FixHost host, List<StepOutcome> steps, StepOutcome step)
    {
        steps.Add(step);
        if (!step.Ok)
        {
            host.Log.Warn("fix: " + step.Step + " failed with 0x" + step.Code.ToString("X8", CultureInfo.InvariantCulture) + " (" + step.CodeName + ").");
        }

        return step.Ok;
    }

    private static async Task<bool> EndAsync(FixHost host, RunState state, List<string> lines, List<StepOutcome> steps)
    {
        if (!state.Touched || state.Adopted || !state.Installed)
        {
            ClearMarker(host, lines, steps);
            return true;
        }

        try
        {
            FixGateResult block = await host.Gate.BlockAsync(CancellationToken.None).ConfigureAwait(false);
            steps.AddRange(block.Steps);
            if (block.Ok)
            {
                lines.Add(state.NewPairUnprotected ? NewPairUnprotectedLine : "Earshot blocked the AirPods again.");
                ClearMarker(host, lines, steps);
                return true;
            }

            if (state.NewPairUnprotected)
            {
                lines.Add(NewPairUnprotectedLine);
            }

            lines.Add("Could not block the AirPods again: " + block.Summary + ". Earshot tries again the next time it starts.");
        }
        catch (Exception ex)
        {
            lines.Add("Could not block the AirPods again: " + ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "). Earshot tries again the next time it starts.");
            host.Log.Error("fix: the closing block stopped with " + ex.GetType().Name, ex);
        }

        return false;
    }

    // The raw codes of what the administrator run did, from the file it leaves in the machine folder, named by what each step
    // was and never by an id.
    private static void AddChangeLines(List<string> lines, FixHost host, string planSha)
    {
        if (host.MachineFolder is null)
        {
            lines.Add("The step codes of the administrator run are in Earshot's log.");
            return;
        }

        FixResultRead read = FixResultFile.Read(host.MachineFolder, planSha);
        if (!read.Ok)
        {
            lines.Add("The step codes of the administrator run could not be read (" + read.Problem + ").");
            return;
        }

        foreach (StepOutcome step in read.Steps.Where(s => s.Step.StartsWith("fix-", StringComparison.Ordinal)))
        {
            lines.Add(StepLine(step));
        }
    }

    internal static string StepLine(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        int colon = step.Step.IndexOf(':', StringComparison.Ordinal);
        string kind = colon < 0 ? step.Step : step.Step[..colon];
        string what = colon < 0 ? "" : " " + FixLabels.NodeKind(step.Step[(colon + 1)..]).ToLowerInvariant();
        string verb = kind switch
        {
            "fix-enable" => "Turned on",
            "fix-clear-flag" => "Cleared the saved off setting of",
            "fix-services" => "Turned services back on",
            _ => kind,
        };
        return (step.Ok ? "Done: " : "Could not: ") + verb + what + ": " + step.CodeName + " (" + step.Code.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
