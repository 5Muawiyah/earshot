using System.Globalization;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

// The real elevator: one administrator prompt, running `fix-apply` from the installed Earshot.exe when there is one and from
// this copy otherwise. A copy that is not installed is in a folder the user can write, so the prompt names that program; the
// elevated run itself only puts back what its own re-derived plan holds.
internal sealed class ShellRunasFixElevator : IFixElevator
{
    private readonly IElevatedLauncher _launcher;
    private readonly string _executable;

    public ShellRunasFixElevator(IElevatedLauncher launcher, string executable)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _launcher = launcher;
        _executable = executable;
    }

    public Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (consent.Kind != FixConsentKind.Changes)
        {
            throw new ArgumentException("The administrator run needs the consent of the first question.", nameof(consent));
        }

        // The checks of the elevated side repeat these; refusing here keeps a malformed request from reaching the prompt.
        if (!FixPlanFileReader.IsPlanPath(planPath, out string problem) || !FileManifest.IsSha256(planSha256))
        {
            return Task.FromResult(new ElevatedRun(null, StepOutcomes.NotAttempted("runas", "The plan file or its hash is not valid: " + problem)));
        }

        return _launcher.RunAsync(_executable, FixApply.Verb + " \"" + planPath + "\" " + planSha256, ct);
    }
}

// Sends the block and the adoption through the \Earshot\Gate task, as the tray does, and confirms each by reading the nodes
// back: the task's result is advisory.
internal sealed class TaskGateFixGate : IFixGate
{
    private readonly ISystemWorker _worker;
    private readonly TaskSchedulerGate _gate;
    private readonly GateStore _store;
    private readonly INodeReader _nodes;
    private readonly ILog _log;

    public TaskGateFixGate(ISystemWorker worker, TaskSchedulerGate gate, GateStore store, INodeReader nodes, ILog log)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(log);
        _worker = worker;
        _gate = gate;
        _store = store;
        _nodes = nodes;
        _log = log;
    }

    public Task<FixGateResult> BlockAsync(CancellationToken ct) =>
        _worker.RunAsync(token => Run(GateVerbs.Block, null, token), ct);

    public Task<FixGateResult> AdoptDeviceAsync(string address12, CancellationToken ct)
    {
        if (!BoundaryValidation.IsAddress12(address12))
        {
            return Task.FromResult(new FixGateResult(false, "The address is not valid", [StepOutcomes.NotAttempted(GateVerbs.AdoptDevice, "The address is not 12 upper-case hex characters.")], null));
        }

        return _worker.RunAsync(token => Run(GateVerbs.AdoptDevice, address12, token), ct);
    }

    private FixGateResult Run(string verb, string? address, CancellationToken ct)
    {
        GateRunResult run = _gate.Run(TaskPlan.GateTaskName, verb, Guid.NewGuid().ToString("N"), address, TaskSchedulerGate.GateTimeout, ct);
        var steps = new List<StepOutcome>(run.Steps);
        if (run.Status is not null)
        {
            steps.AddRange(run.Status.Steps);
        }

        int? exit = run.Status?.ExitCode;
        GateRead<DeviceIdentity> identity = _store.ReadDevice();
        if (!identity.IsOk || identity.Value is null)
        {
            steps.Add(identity.Step);
            return new FixGateResult(false, "Earshot's record of the AirPods could not be read", steps, exit);
        }

        // Ground truth: the nodes as they read now, for the AirPods the record pins.
        NodeReadResult read = new NodeStateReader(_nodes).Read(identity.Value.ContainerId, identity.Value.Address);
        steps.AddRange(read.Steps.Where(s => !s.Ok));
        bool pinMoved = address is null || string.Equals(identity.Value.Address, address, StringComparison.Ordinal);
        bool blocked = BlockStateClassifier.IsFullyBlocked(read);
        bool ok = run.Outcome == GateRunOutcome.Completed && pinMoved && blocked;
        string summary = !pinMoved ? "the gate did not pin the new pair"
            : !blocked ? "the nodes do not read as blocked"
            : run.Outcome != GateRunOutcome.Completed ? "the gate task ended with " + run.Outcome
            : "done";
        _log.Info("fix: " + verb + " " + (ok ? "ok" : summary) + (exit is int e ? " (" + e.ToString(CultureInfo.InvariantCulture) + ")" : "") + ".");
        return new FixGateResult(ok, summary, steps, exit);
    }
}

// What a Fix run uses when Earshot is not installed: it is never asked to do anything, because nothing is installed to ask.
internal sealed class NoInstalledGate : IFixGate
{
    public Task<FixGateResult> BlockAsync(CancellationToken ct) =>
        Task.FromResult(new FixGateResult(false, "Earshot is not installed", [StepOutcomes.NotAvailable("fix-block", "Earshot is not installed.")], null));

    public Task<FixGateResult> AdoptDeviceAsync(string address12, CancellationToken ct) =>
        Task.FromResult(new FixGateResult(false, "Earshot is not installed", [StepOutcomes.NotAvailable(GateVerbs.AdoptDevice, "Earshot is not installed.")], null));
}
