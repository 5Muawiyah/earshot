using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// The waiting budgets of the hand-back service's shut-down block. Every figure is a choice made for the pre-shutdown
// window, not a measurement: Windows waits 10,000 ms for a service that handles the pre-shutdown control (its
// documented default since Windows 10 build 15063), so the work stops 2,000 ms short of that, which leaves room for
// the status file, the prune, the log and the report that the service has stopped.
//
// The block itself is never cut short (a disable already sent stays sent), so the waits that come before it are sized to
// leave it room. Its length is known from one sample only: two runs on the owner's PC took 5,460 and 5,429 ms when a
// driver refused one node while the AirPods were playing, and about 300 ms when nothing was playing. BlockReserve is that
// sample rounded up. It is a sample, not a limit: a driver that takes longer to refuse a node would take the run past
// its budget, and nothing here can prevent that. What is sized is everything that is not the block, so that the two lock
// waits together never take more than Total minus BlockReserve, whichever of them takes it, and the block that follows
// finishes inside Total when it takes no longer than the sample.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_preshutdown_info
internal static class PreshutdownBudget
{
    public static readonly TimeSpan Total = TimeSpan.FromMilliseconds(8_000);

    // Left for the block itself when the locks are waited for. The sample described above, rounded up.
    public static readonly TimeSpan BlockReserve = TimeSpan.FromMilliseconds(5_500);

    // What the two lock waits may take between them.
    public static readonly TimeSpan WaitBudget = Total - BlockReserve;

    // A gate run the tray started may still hold the run lock while it blocks the same nodes. Waiting for it may take the
    // whole of the wait budget and no more.
    public static readonly TimeSpan RunLockWait = WaitBudget;

    // One more try at a node a driver refused to let go, and the time that must still be left for it to be worth it: the
    // delay, then a block of the length the reserve allows for, so a second try that is refused again still ends inside
    // the budget.
    public static readonly TimeSpan VetoRetryDelay = TimeSpan.FromMilliseconds(1_000);
    public static readonly TimeSpan VetoRetryRoom = VetoRetryDelay + BlockReserve;
}

// What a shut-down block did, for the service's log. Steps is what was written to the status file.
internal sealed record PreshutdownResult(
    GateExitCode Outcome,
    string? State,
    string Reason,
    bool StatusWritten,
    IReadOnlyList<StepOutcome> Steps)
{
    // A disable was sent, and when.
    public bool BlockSent { get; init; }

    public DateTimeOffset? BlockSentUtc { get; init; }

    // Nodes a driver refused to let go (CR_REMOVE_VETOED), and the wait before they were sent again, if they were.
    public int VetoedNodes { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    // Set when the run ran out of its budget while it waited for a lock, and names the lock.
    public string? CutShortWaitingFor { get; init; }

    // How the write of the status file went.
    public StepOutcome? StatusStep { get; init; }
}

// The shut-down block the hand-back service runs.
internal sealed partial class GateActions
{
    private const string PreshutdownStep = "preshutdown";
    private const string DisableStepPrefix = "cm-disable:";

    // For tests: the wait before the nodes a driver refused are sent again. Returning false skips the second try.
    internal Func<TimeSpan, bool> RetryWait { get; init; } = DeviceChangeLock.SleepAndContinue;

    // The gate the hand-back service runs: the real node API, the machine-wide run lock and no Bluetooth API, so the
    // service holds nothing that could turn a Bluetooth service on or off.
    public static GateActions ForPreshutdown(string machineFolder, ILog log) =>
        new(new CfgMgr32NodeApi(), MachineStore(machineFolder), new NtfsFolderSecurity(), log, TimeProvider.System, new MachineGateMutex());

    private sealed class PreshutdownDecision
    {
        public GateExitCode Outcome { get; set; } = GateExitCode.Failed;

        public string? State { get; set; }

        public string Reason { get; set; } = "";

        public bool BlockSent { get; set; }

        public DateTimeOffset? BlockSentUtc { get; set; }

        public int Vetoed { get; set; }

        public TimeSpan? RetryAfter { get; set; }

        public string? CutShort { get; set; }
    }

    // Blocks the pinned device's nodes if they are not blocked already, inside the budget that ends at deadline, and
    // writes status-<nonce>.json with the verb preshutdown. The only device call it can make is ApplyBlock, which
    // disables and never enables, so nothing here can leave the nodes more enabled than it found them. It is a method
    // of its own and never a verb of Run: no command line, task or request can reach it.
    //
    // A run that cannot enter the run lock, or reads a config.json or device.json that is missing or not valid, or
    // whose setting is off, changes nothing and says why. A machine folder that fails its check is not trusted at all:
    // nothing is read from it and nothing is written into it.
    public PreshutdownResult RunPreshutdown(string nonce, DateTimeOffset deadline)
    {
        if (!BoundaryValidation.IsNonce(nonce))
        {
            throw new ArgumentException("A status file needs a 32 character lower-case hex nonce.", nameof(nonce));
        }

        DateTimeOffset started = deadline - PreshutdownBudget.Total;
        var steps = new List<StepOutcome>();
        TimeSpan runWait = Lesser(PreshutdownBudget.RunLockWait, Waitable(deadline));
        bool runWaitCutShort = Waitable(deadline) < PreshutdownBudget.RunLockWait;
        using IDisposable? held = _runLock.TryEnter(runWait, steps);

        if (!FolderIsSecure(steps))
        {
            foreach (StepOutcome step in steps.Where(s => !s.Ok))
            {
                _log.Error("gate preshutdown: " + Describe(step));
            }

            return new PreshutdownResult(GateExitCode.FolderNotSecure, null, "the machine folder did not pass its check", false, steps);
        }

        var decision = new PreshutdownDecision();
        try
        {
            if (held is null)
            {
                decision.Outcome = GateExitCode.Failed;
                decision.Reason = "another Earshot run held the lock";
                decision.CutShort = runWaitCutShort ? "the run lock" : null;
            }
            else
            {
                DecidePreshutdown(decision, steps, deadline);
            }
        }
        catch (Exception ex)
        {
            steps.Add(ElevatedFailure.Step(PreshutdownStep, ex));
            _log.Error("gate preshutdown stopped with " + ex.GetType().Name + " after " + steps.Count + " steps.", ex);
            decision.Outcome = GateExitCode.Failed;
            decision.Reason = "stopped by " + ex.GetType().Name;
        }

        if (!steps.Any(s => s.Step == PreshutdownStep))
        {
            steps.Add(StepOutcomes.NotAttempted(PreshutdownStep, decision.Reason));
        }

        if (held is not null)
        {
            steps.AddRange(_store.PruneStatusFiles(_time.GetUtcNow()));
        }

        GateExitCode outcome = decision.Outcome;
        var status = new GateStatusFile(
            GateStore.SchemaVersion, nonce, GateVerbs.Preshutdown, started, _time.GetUtcNow(),
            GateExitCodes.ResultName(outcome), (int)outcome, decision.State, steps, StepsTruncated: false);
        StepOutcome written = _store.WriteStatus(status);
        if (!written.Ok)
        {
            _log.Error("gate preshutdown: " + Describe(written));
            if (outcome == GateExitCode.Success)
            {
                outcome = GateExitCode.StatusNotWritten;
            }
        }

        foreach (StepOutcome step in steps.Where(s => !s.Ok))
        {
            _log.Warn("gate preshutdown " + nonce + ": " + Describe(step));
        }

        _log.Info("gate preshutdown " + nonce + ": " + GateExitCodes.ResultName(outcome) + " (" + (int)outcome + ")" +
                  (decision.State is null ? "" : ", state " + decision.State) + ".");
        return new PreshutdownResult(outcome, decision.State, decision.Reason, written.Ok, steps)
        {
            BlockSent = decision.BlockSent,
            BlockSentUtc = decision.BlockSentUtc,
            VetoedNodes = decision.Vetoed,
            RetryAfter = decision.RetryAfter,
            CutShortWaitingFor = decision.CutShort,
            StatusStep = written,
        };
    }

    // Everything between "the run lock is held and the folder is trusted" and the status file.
    private void DecidePreshutdown(PreshutdownDecision decision, List<StepOutcome> steps, DateTimeOffset deadline)
    {
        if (!ConfigPresent(steps))
        {
            decision.Outcome = GateExitCode.NoConfig;
            decision.Reason = "not set up";
            return;
        }

        // A missing or invalid setting is never taken as permission: the file must read, and both settings must be on.
        GateRead<GateConfig> config = _store.ReadConfig();
        steps.Add(config.Step);
        if (!config.IsOk || config.Value is null)
        {
            decision.Outcome = GateExitCode.Failed;
            decision.Reason = "config.json is not valid";
            return;
        }

        if (!config.Value.BlockAtBoot)
        {
            Nothing(decision, steps, "Block at boot is off");
            return;
        }

        if (!config.Value.HandBackAtShutdown)
        {
            Nothing(decision, steps, "Hand back is off");
            return;
        }

        DeviceIdentity? identity = ReadIdentity(steps);
        if (identity is null)
        {
            decision.Outcome = GateExitCode.NoIdentity;
            decision.Reason = "device.json is missing or not valid";
            return;
        }

        // The tray's own hand-back, or the idle rule, may have blocked the nodes already. The device tree says so and
        // no user can forge it, so there is no message between the two. A read that failed says nothing about the
        // nodes and is not taken as "not blocked".
        NodeReadResult read = new NodeStateReader(_nodes).Read(identity.ContainerId, identity.Address);
        if (!read.Listed)
        {
            steps.AddRange(read.Steps.Where(s => !s.Ok));
            decision.Outcome = GateExitCode.Failed;
            decision.State = nameof(BlockState.Unknown);
            decision.Reason = "the nodes could not be read";
            return;
        }

        if (BlockStateClassifier.IsFullyBlocked(read))
        {
            steps.Add(new StepOutcome(PreshutdownStep, true, 0, "S_OK", "already blocked"));
            decision.Outcome = GateExitCode.Success;
            decision.State = nameof(BlockState.Blocked);
            decision.Reason = "already blocked";
            return;
        }

        BlockNodes(decision, steps, identity, deadline);
    }

    private static void Nothing(PreshutdownDecision decision, List<StepOutcome> steps, string reason)
    {
        steps.Add(StepOutcomes.NotAttempted(PreshutdownStep, reason));
        decision.Outcome = GateExitCode.Success;
        decision.Reason = reason;
    }

    private void BlockNodes(PreshutdownDecision decision, List<StepOutcome> steps, DeviceIdentity identity, DateTimeOffset deadline)
    {
        TimeSpan changeWait = Lesser(DeviceChangeLock.NodeChangeLockTimeout, Waitable(deadline));
        using DeviceChangeLock? changing = DeviceChangeLock.TryAcquire(_store.Folder, DeviceChangeLockAccess.For(_nodes), changeWait, LockWait, steps);
        if (changing is null)
        {
            decision.Outcome = GateExitCode.Failed;
            decision.Reason = "another device change held the lock";
            decision.CutShort = changeWait < DeviceChangeLock.NodeChangeLockTimeout ? "the device change lock" : null;
            return;
        }

        NodeScanResult scan = NodeScan.FindTargets(_nodes, identity.ContainerId, identity.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            decision.Outcome = GateExitCode.Failed;
            decision.State = nameof(BlockState.Unknown);
            decision.Reason = "the nodes could not be listed";
            return;
        }

        if (scan.Targets.Count == 0)
        {
            VerbResult none = NoTargets(scan);
            decision.Outcome = none.Outcome;
            decision.State = none.State;
            decision.Reason = "no node matched the pinned device";
            return;
        }

        DateTimeOffset sent = _time.GetUtcNow();
        int before = steps.Count;
        NodeChangeSummary summary = ApplyBlock(_nodes, scan.Targets, steps);
        decision.BlockSent = true;
        decision.BlockSentUtc = sent;
        steps.Add(new StepOutcome(PreshutdownStep, true, 0, "S_OK", "block sent at " + FormatUtc(sent)));

        // A driver may refuse to let one node go while it is still in use (CR_REMOVE_VETOED); with the device node
        // disabled the link is down, so a second try a moment later often succeeds. One retry, of those nodes only,
        // and only when the budget has room for it.
        List<string> vetoed = steps.Skip(before)
            .Where(s => s.Step.StartsWith(DisableStepPrefix, StringComparison.Ordinal) && s.Code == (int)CfgMgr32.CR_REMOVE_VETOED)
            .Select(s => s.Step[DisableStepPrefix.Length..])
            .ToList();
        decision.Vetoed = vetoed.Count;
        if (vetoed.Count > 0 && Remaining(deadline) >= PreshutdownBudget.VetoRetryRoom && RetryWait(PreshutdownBudget.VetoRetryDelay))
        {
            List<TargetNode> again = scan.Targets.Where(t => vetoed.Contains(t.InstanceId, StringComparer.Ordinal)).ToList();
            NodeChangeSummary retry = ApplyBlock(_nodes, again, steps);
            summary = new NodeChangeSummary(
                summary.Total, summary.Changed + retry.Changed, summary.Already + retry.Already,
                summary.NotPresent + retry.NotPresent, summary.Failed - again.Count + retry.Failed);
            decision.RetryAfter = PreshutdownBudget.VetoRetryDelay;
        }

        decision.Outcome = WithUnreadable(summary.Outcome, scan);
        decision.State = ReadState(identity);
        decision.Reason = "block sent";
    }

    // What may still be spent waiting for a lock: what remains of the budget, less the room the block needs.
    private TimeSpan Waitable(DateTimeOffset deadline) => Greater(TimeSpan.Zero, Remaining(deadline) - PreshutdownBudget.BlockReserve);

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        TimeSpan left = deadline - _time.GetUtcNow();
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private static TimeSpan Lesser(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Greater(TimeSpan a, TimeSpan b) => a > b ? a : b;

    internal static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
