using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Repair;

// The elevated half of Fix connection: `Earshot.exe fix-apply <planfile> <plan-sha256>`.
//
// The command line is attacker-controlled (any program the user runs can ask for the administrator prompt), so the plan file
// is only ever a request to do less. The run reads the file once, checks its hash against the one on the command line, builds
// the plan again from the machine itself (the AirPods the identity rule finds, Earshot's records, Earshot's own node choice),
// and changes only what both name. A node, a service or a device the file adds is ignored; a file that is missing, changed
// or malformed changes nothing. Nothing is ever disabled, blocked or paired here: the run only puts back what Earshot may have
// changed, and every step records its raw code.

internal sealed record FixApplyRequest(string PlanPath, string PlanSha256);

internal sealed record FixApplied(string InstanceId, FixNodeAction Action);

internal sealed record FixApplyResult(GateExitCode Outcome, IReadOnlyList<StepOutcome> Steps, IReadOnlyList<FixApplied> Changes);

internal sealed class FixApplyDeps
{
    public required INodeApi Nodes { get; init; }

    public required GateStore Store { get; init; }

    public required IFolderSecurity Folders { get; init; }

    public required IGateRunLock RunLock { get; init; }

    public IBluetoothServiceApi? Bluetooth { get; init; }

    public required ILog Log { get; init; }

    public required IFixLeftovers LeftoversReader { get; init; }

    // A settings file or an installed program: Earshot's footprint beside its records.
    public Func<bool> OtherFootprint { get; init; } = static () => false;

    // The wait between device change lock attempts; false ends the wait.
    public Func<TimeSpan, bool> Wait { get; init; } = DeviceChangeLock.SleepAndContinue;

    public TimeSpan LockWait { get; init; } = DeviceChangeLock.NodeChangeLockTimeout;
}

internal static class FixApply
{
    public const string Verb = "fix-apply";

    public const string Usage = "Usage: Earshot.exe fix-apply <planFile> <sha256>.";

    // Exactly [fix-apply, planFile, sha256]. Only the strings are checked here.
    public static bool TryParseArgs(IReadOnlyList<string> args, [NotNullWhen(true)] out FixApplyRequest? request, [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;
        if (args.Count != 3 || args[0] != Verb)
        {
            problem = "wrong number of arguments";
            return false;
        }

        if (!FixPlanFileReader.IsPlanPath(args[1], out string shape))
        {
            problem = shape;
            return false;
        }

        if (!FileManifest.IsSha256(args[2]))
        {
            problem = "the hash is not 64 hex characters";
            return false;
        }

        request = new FixApplyRequest(args[1], args[2].ToUpperInvariant());
        problem = null;
        return true;
    }

    public static FixApplyResult Run(FixApplyRequest request, FixApplyDeps deps)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deps);
        var steps = new List<StepOutcome>();
        var changes = new List<FixApplied>();

        FixPlanRead file = FixPlanFileReader.ReadOnce(request.PlanPath, request.PlanSha256);
        if (!file.Ok || file.Selection is null)
        {
            steps.Add(StepOutcomes.NotAttempted("fix-plan-read", file.Problem));
            deps.Log.Warn("fix-apply: " + file.Problem);
            return new FixApplyResult(GateExitCode.Rejected, steps, changes);
        }

        string machine = deps.Store.Folder;
        bool machineExists = Directory.Exists(machine);
        if (machineExists && !FolderTrust.IsTrusted(deps.Folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", steps))
        {
            steps.Add(StepOutcomes.NotAttempted("fix-apply", machine + " is not safe, so its records were not used and nothing was changed."));
            return new FixApplyResult(GateExitCode.FolderNotSecure, steps, changes);
        }

        using IDisposable? running = machineExists ? deps.RunLock.TryEnter(deps.LockWait, steps) : NoGateRunLock.Instance.TryEnter(deps.LockWait, steps);
        if (running is null)
        {
            steps.Add(StepOutcomes.NotAttempted("fix-apply", "Another Earshot change is running, so nothing was changed. Run Fix connection again."));
            return new FixApplyResult(GateExitCode.Failed, steps, changes);
        }

        using DeviceChangeLock? changing = machineExists
            ? DeviceChangeLock.TryAcquire(machine, DeviceChangeLockAccess.For(deps.Nodes), deps.LockWait, deps.Wait, steps)
            : null;
        if (machineExists && changing is null)
        {
            steps.Add(StepOutcomes.NotAttempted("fix-apply", "The device change lock could not be taken, so nothing was changed. Run Fix connection again."));
            return new FixApplyResult(GateExitCode.Failed, steps, changes);
        }

        var records = new StoreFixRecords(deps.Store, deps.OtherFootprint);
        FixPlanResult derived = FixPlanner.Build(deps.Nodes, records, deps.LeftoversReader);
        steps.AddRange(derived.Steps.Where(s => !s.Ok));
        if (!derived.Listed)
        {
            return new FixApplyResult(GateExitCode.Failed, steps, changes);
        }

        // The intersection: a step of the machine's own plan, named by the file as well.
        List<FixNodeStep> wanted = derived.Plan.Devices.SelectMany(d => d.Nodes)
            .Where(n => file.Selection.Nodes.Contains((n.InstanceId.ToUpperInvariant(), n.Action)))
            .ToList();
        int refused = file.Selection.Nodes.Count - wanted.Count;
        if (refused > 0)
        {
            steps.Add(StepOutcomes.NotAttempted("fix-plan-extra", refused.ToString(CultureInfo.InvariantCulture) + " node(s) in the plan file are not what this PC's own plan holds, so they were not changed."));
        }

        // Flags first: nodes out of the tree are cleared before a node that is enabled can bring them back flagged.
        foreach (FixNodeStep step in wanted.Where(n => n.Action == FixNodeAction.ClearFlag))
        {
            ClearFlag(deps.Nodes, step.InstanceId, steps, changes);
        }

        foreach (FixNodeStep step in wanted.Where(n => n.Action == FixNodeAction.Enable).OrderBy(n => !n.InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)).ThenBy(n => n.InstanceId, StringComparer.Ordinal))
        {
            Enable(deps.Nodes, step.InstanceId, steps, changes);
        }

        RestoreServices(request, deps, file.Selection, derived.Plan, steps);

        // Only the steps that change something decide the result; the notes about ignored file entries do not.
        bool IsChange(StepOutcome s) => s.Step.StartsWith("fix-enable:", StringComparison.Ordinal) || s.Step.StartsWith("fix-clear-flag:", StringComparison.Ordinal) || s.Step == "fix-services";
        GateExitCode outcome = steps.Where(IsChange).All(s => s.Ok)
            ? GateExitCode.Success
            : changes.Count > 0 ? GateExitCode.Partial : GateExitCode.Failed;
        var result = new FixApplyResult(outcome, steps, changes);
        if (machineExists)
        {
            StepOutcome written = FixResultFile.Write(machine, request.PlanSha256, result);
            if (!written.Ok)
            {
                deps.Log.Warn("fix-apply: " + GateActions.Describe(written));
            }
        }

        deps.Log.Info("fix-apply: " + GateExitCodes.ResultName(outcome) + ", " + changes.Count.ToString(CultureInfo.InvariantCulture) + " change(s).");
        return result;
    }

    private static void ClearFlag(INodeApi nodes, string id, List<StepOutcome> steps, List<FixApplied> changes)
    {
        ConfigFlagsChange change = nodes.ClearDisabledFlag(id);
        string detail = "stage " + change.Stage + ", flags 0x" + change.Before.ToString("X", CultureInfo.InvariantCulture) + " to 0x" + change.After.ToString("X", CultureInfo.InvariantCulture);
        bool ok = change.Ok && !change.DisabledAfter;
        steps.Add(StepOutcomes.FromWin32("fix-clear-flag:" + id, change.Win32Error, detail, ok));
        if (!ok)
        {
            return;
        }

        changes.Add(new FixApplied(id, FixNodeAction.ClearFlag));

        // A node that came back while the flag was cleared is in the tree now and may be at problem 22 from before.
        if (nodes.Locate(id, includeNonPresent: false, out uint devInst) == CfgMgr32.CR_SUCCESS &&
            nodes.GetStatus(devInst, out uint status, out uint problem) == CfgMgr32.CR_SUCCESS &&
            (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED)
        {
            Enable(nodes, id, steps, changes);
        }
    }

    private static void Enable(INodeApi nodes, string id, List<StepOutcome> steps, List<FixApplied> changes)
    {
        uint cr = nodes.Locate(id, includeNonPresent: false, out uint devInst);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("fix-enable:" + id, cr, "Not in the tree now, so it was not enabled."));
            return;
        }

        cr = nodes.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("fix-enable:" + id, cr, "Its status could not be read, so it was not enabled."));
            return;
        }

        if ((status & CfgMgr32.DN_HAS_PROBLEM) == 0 || problem != CfgMgr32.CM_PROB_DISABLED)
        {
            steps.Add(StepOutcomes.FromConfigRet("fix-enable:" + id, CfgMgr32.CR_SUCCESS, "Already enabled."));
            return;
        }

        cr = nodes.Enable(devInst);
        steps.Add(StepOutcomes.FromConfigRet("fix-enable:" + id, cr));
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return;
        }

        changes.Add(new FixApplied(id, FixNodeAction.Enable));

        // An enable can leave CONFIGFLAG_DISABLED set, and the node would come back disabled; clear it too.
        if (nodes.GetConfigFlags(devInst, out uint flags) == CfgMgr32.CR_SUCCESS && (flags & CfgMgr32.CONFIGFLAG_DISABLED) != 0)
        {
            ConfigFlagsChange change = nodes.ClearDisabledFlag(id);
            steps.Add(StepOutcomes.FromWin32("fix-clear-flag:" + id, change.Win32Error, "after the enable, stage " + change.Stage, change.Ok && !change.DisabledAfter));
        }
    }

    // Turns back on the services protection.json lists, only when the file names exactly the set the machine's plan holds:
    // the restore turns on everything the record lists, so a file that names fewer cannot narrow it.
    private static void RestoreServices(FixApplyRequest request, FixApplyDeps deps, FixPlanSelection selection, FixPlan derived, List<StepOutcome> steps)
    {
        var machineSet = derived.Services.Select(s => s.Service).ToHashSet();
        if (machineSet.Count == 0)
        {
            return;
        }

        if (!selection.Services.SetEquals(machineSet))
        {
            steps.Add(StepOutcomes.NotAttempted("fix-services", "The plan file does not name the services this PC's record lists, so none was turned back on."));
            return;
        }

        GateRead<DeviceIdentity> identity = deps.Store.ReadDevice();
        if (!identity.IsOk || identity.Value is null)
        {
            steps.Add(StepOutcomes.NotAttempted("fix-services", "Earshot's record of the AirPods could not be read, so no service was turned back on."));
            return;
        }

        var ctx = new GateRunContext("fix-apply", request.PlanSha256[..32].ToLowerInvariant(), identity.Value, deps.Nodes, deps.Store, deps.Log, steps, deps.Bluetooth);
        if (!GateActions.TryRestoreProtection(ctx))
        {
            steps.Add(StepOutcomes.NotAvailable("fix-services", "This run has no Bluetooth service API, so no service was turned back on."));
            return;
        }

        steps.Add(new StepOutcome("fix-services", ctx.Outcome == GateExitCode.Success, (int)ctx.Outcome, GateExitCodes.ResultName(ctx.Outcome), "Services turned back on from the record."));
    }
}
