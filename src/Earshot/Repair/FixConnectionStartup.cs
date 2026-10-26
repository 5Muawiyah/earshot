using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

// What the tray does when it starts about Fix connection: answer a run that stopped half way, leave a run that is still
// pairing alone, and say when paired AirPods are not the ones Earshot protects. None of it asks the person anything, and none
// of it pins, adopts or changes a node except the one normal block a stale marker is answered with.

// Answers a marker left by a run that has gone (see FixRecovery) with the normal block, once, when the tray starts.
internal sealed class FixRecoveryAtStart : IStartRecovery
{
    private readonly FixRecovery _recovery;
    private readonly FixRecovery? _administrators;
    private readonly IFixRecords _records;
    private readonly IFixGate _gate;
    private readonly ILog _log;

    // administrators: the marker a Fix run started as administrator keeps in the machine folder (Program.FixWindowFile). The
    // tray can read it but not remove it.
    public FixRecoveryAtStart(FixRecovery recovery, IFixRecords records, IFixGate gate, ILog log, FixRecovery? administrators = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(log);
        _recovery = recovery;
        _administrators = administrators;
        _records = records;
        _gate = gate;
        _log = log;
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        bool changed = await RunAsync(_recovery, ct).ConfigureAwait(false);
        if (_administrators is not null)
        {
            changed |= await RunAsync(_administrators, ct).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> RunAsync(FixRecovery recovery, CancellationToken ct)
    {
        FixRecoveryOutcome outcome = await recovery.RecoverAsync(_records, _gate, ct).ConfigureAwait(false);
        bool stays = outcome is FixRecoveryOutcome.Blocked or FixRecoveryOutcome.NothingToBlock && recovery.Read().State != FixRecoveryState.None;

        switch (outcome)
        {
            case FixRecoveryOutcome.NoMarker:
                break;
            case FixRecoveryOutcome.WindowOpen:
                _log.Info("fix: a Fix connection run is still pairing the AirPods, so nothing is blocked for it.");
                break;
            case FixRecoveryOutcome.Blocked:
                _log.Info("fix: a Fix connection run that did not finish was answered with the block.");
                break;
            case FixRecoveryOutcome.NothingToBlock:
                _log.Info("fix: a Fix connection run left a marker and Earshot has nothing to block" + (stays ? "." : ", so the marker was cleared."));
                break;
            default:
                _log.Warn("fix: a Fix connection run that did not finish could not be answered with the block. It is tried again at the next start.");
                break;
        }

        if (stays)
        {
            _log.Warn("fix: the marker could not be removed, so it is answered again at the next start. A Fix connection run started as administrator removes it once it reaches its pairing step.");
        }

        return outcome == FixRecoveryOutcome.Blocked;
    }
}

// The window of a run that is pairing again, read from the marker the run writes. A missing, damaged or old marker is not an
// open window.
internal sealed class FixMarkerRePairWindow : IRePairWindow
{
    private readonly FixRecovery _recovery;
    private readonly FixRecovery? _administrators;

    // administrators: the marker of a Fix run started as administrator, in the machine folder.
    public FixMarkerRePairWindow(FixRecovery recovery, FixRecovery? administrators = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        _recovery = recovery;
        _administrators = administrators;
    }

    public bool IsOpen => _recovery.Read().State == FixRecoveryState.WindowOpen || _administrators?.Read().State == FixRecoveryState.WindowOpen;
}

// Paired AirPods that are not the pinned ones, from the identity rule and the pin record. Reads only.
internal sealed class AirPodsProtectionCheck : IProtectionCheck
{
    private readonly INodeReader _nodes;
    private readonly IFixRecords _records;

    public AirPodsProtectionCheck(INodeReader nodes, IFixRecords records)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(records);
        _nodes = nodes;
        _records = records;
    }

    public Task<ProtectionReading> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    // A pairing that was removed leaves its nodes behind, so only a set whose device node is in the tree now counts as paired.
    public ProtectionReading Read()
    {
        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
        if (!found.Listed)
        {
            return ProtectionReading.Nothing;
        }

        GateRead<DeviceIdentity> device = _records.ReadDevice();
        bool pinned = device.IsOk && device.Value is not null;
        Guid pinnedContainer = pinned ? device.Value!.ContainerId : Guid.Empty;
        int unprotected = FixRules.UnprotectedAirPods(found, pinnedContainer).Count(c => c.DevicePresent);
        return new ProtectionReading(true, pinned, unprotected);
    }
}
