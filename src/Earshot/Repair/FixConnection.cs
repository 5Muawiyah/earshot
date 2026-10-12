using System.Globalization;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

// Fix connection, as the unelevated program runs it.
//
//   1. Plan, from reads only: the AirPods the identity rule finds, Earshot's records, what Earshot's own node choice picks.
//   2. Print the plan. With nobody at the screen (no input desktop, a different desktop, not interactive) stop here: nothing
//      is asked, elevated or changed, and the exit code says "plan only".
//   3. First question (an on-screen dialog): every change. On OK, one administrator prompt runs `fix-apply`, which builds the
//      plan again itself and applies the part both agree on.
//   4. Second question (a separate dialog): remove the AirPods from Windows and pair them again, by name. On OK, in this
//      session: unpair, scan, show Apple's instruction, pick, pair, check the new device with the identity rule, adopt it
//      through one gate run when Earshot is installed.
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
    Done,
    Failed,
}

internal sealed record FixResult(FixOutcome Outcome, IReadOnlyList<string> Lines, IReadOnlyList<StepOutcome> Steps)
{
    public int ExitCode => Outcome switch
    {
        FixOutcome.Usage => Earshot.ExitCodes.Usage,
        FixOutcome.PlanOnly => Earshot.ExitCodes.PlanOnly,
        FixOutcome.Failed => Earshot.ExitCodes.Software,
        _ => Earshot.ExitCodes.Ok,
    };
}

internal static class FixConnection
{
    public const string Usage = "Usage: Earshot.exe fix [--out <path>]. Earshot asks on the screen before it changes anything, so it takes no yes, force or consent option.";

    public const string PlanOnlyNote = "Nothing was changed. Earshot asks on the screen before it changes anything, so run Fix connection where a person can answer.";

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

        var dialog = new FixConsentDialog(host.Prompter, host.Desktop);
        if (!dialog.CanAsk())
        {
            host.Output.WriteLine(PlanOnlyNote);
            return new FixResult(FixOutcome.PlanOnly, [PlanOnlyNote], steps);
        }

        FixResult result = await RunWithPersonAsync(dialog, plan, host, steps, ct).ConfigureAwait(false);
        host.Output.WriteLine(string.Join(Environment.NewLine, result.Lines));
        host.RePairUi.ShowResult(string.Join(Environment.NewLine, result.Lines));
        return result;
    }

    // What a run has done so far, for the closing block to read.
    private sealed class RunState
    {
        public bool Touched { get; set; }

        public bool Adopted { get; set; }

        public bool Installed { get; init; }
    }

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
        return new FixResult(ended ? outcome : FixOutcome.Failed, lines, steps);
    }

    private static async Task<FixOutcome> ChangeAndRePairAsync(
        FixConsentDialog dialog, FixPlan plan, FixHost host, RunState state, List<string> lines, List<StepOutcome> steps, CancellationToken ct)
    {
        FixPlan work = plan;
        if (!plan.IsEmpty)
        {
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
            try
            {
                run = await host.Elevator.ApplyAsync(first.Consent, file.Path, file.Sha256, ct).ConfigureAwait(false);
            }
            finally
            {
                host.PlanFiles.Delete(file);
            }

            steps.Add(run.Step);
            if (run.ExitCode is not int exit)
            {
                lines.Add("Windows did not start the administrator run: " + run.Step.CodeName + " (" + run.Step.Code.ToString(CultureInfo.InvariantCulture) + "). Nothing was changed.");
                return FixOutcome.Failed;
            }

            state.Touched = true;
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
        else
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
            lines.Add("Declined. The AirPods were left as they are.");
            return FixOutcome.Declined;
        }

        steps.Add(host.Recovery.Begin("repairing"));
        state.Touched = true;
        var repairer = new RePairer(host.Pairing, host.Nodes, host.RePairUi, host.Options, host.Log);
        RePairResult repaired = await repairer.RunAsync(second, target is null ? null : new RePairTarget(target.Address, target.Name), ct).ConfigureAwait(false);
        lines.AddRange(repaired.Lines);
        steps.AddRange(repaired.Steps);
        foreach (PairingCallResult status in repaired.Statuses)
        {
            lines.Add("Status: " + status.Name + " (" + status.Code.ToString(CultureInfo.InvariantCulture) + ").");
        }

        if (repaired.Adoptable is { } candidate)
        {
            if (!state.Installed)
            {
                lines.Add("Earshot is not installed (or Block at boot is off), so nothing was adopted.");
            }
            else
            {
                FixGateResult adoption = await host.Gate.AdoptDeviceAsync(candidate.Address, ct).ConfigureAwait(false);
                steps.AddRange(adoption.Steps);
                state.Adopted = adoption.Ok;
                lines.Add(adoption.Ok
                    ? "Earshot now protects the new pair, and it is blocked at rest."
                    : "Could not adopt the new pair: " + adoption.Summary + (adoption.ExitCode is int code ? " (" + code.ToString(CultureInfo.InvariantCulture) + ")" : "") + ". Earshot is blocking the AirPods again.");
            }
        }

        return repaired.Paired && repaired.Failure == RePairFailure.None && (state.Adopted || !state.Installed) ? FixOutcome.Done : FixOutcome.Failed;
    }

    // The block that ends every path that changed something. An adoption is a pin and a block in one gate run, so it needs no
    // second one. The block is sent with no cancellation: a cancelled run still ends protected. False when the block was
    // needed and did not happen, which leaves the marker for the tray's next start.
    private static async Task<bool> EndAsync(FixHost host, RunState state, List<string> lines, List<StepOutcome> steps)
    {
        if (!state.Touched || state.Adopted || !state.Installed)
        {
            steps.Add(host.Recovery.Clear());
            return true;
        }

        try
        {
            FixGateResult block = await host.Gate.BlockAsync(CancellationToken.None).ConfigureAwait(false);
            steps.AddRange(block.Steps);
            if (block.Ok)
            {
                lines.Add("Earshot blocked the AirPods again.");
                steps.Add(host.Recovery.Clear());
                return true;
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
