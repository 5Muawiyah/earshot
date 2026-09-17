using Earshot.Contracts;

namespace Earshot.AudioProtection;

// What the caller is moving towards.
internal enum ProtectionGoal
{
    Block,          // disable the device nodes, the at-rest state
    Allow,          // enable the device nodes
    SetProtection,  // the user turned Protect audio quality on or off
    Reverify,       // after a connect or a boot: put protection back if Windows reverted it
}

// One step for the caller to run before asking for the next.
internal enum ProtectionAction
{
    Done,
    ReadServices,   // BluetoothEnumerateInstalledServices, non-elevated
    ProtectOn,      // gate protect-on
    ProtectOff,     // gate protect-off
    BlockNodes,     // gate block
    AllowNodes,     // gate allow

    // A target node is disabled (Blocked or Mixed). Run the protect verb for the wanted state anyway: the gate
    // reads the nodes again and, while one is still disabled, changes no service and keeps the wanted state
    // in protection-intent.json (GetPendingIntentAsync reports it) for the next allow. If the nodes were
    // allowed in the meantime, the gate applies the change there and then.
    StoreIntent,

    // The node state could not be read (Unknown). The gate is not asked, since it would change nothing and
    // keep nothing while the nodes are unreadable or not present. The caller keeps the wanted state (the
    // user's setting) as pending itself and passes IntentPending true until an Allow or Reverify applies it.
    KeepIntent,
}

// The device nodes as far as protection cares. Mixed means at least one target node is disabled, which is
// enough to rule out a service change.
internal enum NodePhase { Allowed, Blocked, Mixed, Unknown }

// What is known now. Nodes and Services are the latest reads; Protect is the user's intent; IntentPending
// is true when that intent was kept while it could not be applied (in protection-intent.json after
// StoreIntent, or by the caller after KeepIntent) and has not been applied yet.
internal sealed record ProtectionFacts(NodePhase Nodes, AudioProtectionState Services, bool Protect, bool IntentPending);

// What this sequence has already done. Each device change runs at most once per sequence, so a change
// that does not take cannot loop.
internal readonly record struct ProtectionProgress(bool ServicesFresh, bool ServiceChangeTried, bool NodeChangeTried, bool IntentStored)
{
    public ProtectionProgress After(ProtectionAction action) => action switch
    {
        ProtectionAction.ReadServices => this with { ServicesFresh = true },
        ProtectionAction.ProtectOn or ProtectionAction.ProtectOff => this with { ServicesFresh = false, ServiceChangeTried = true },
        ProtectionAction.BlockNodes or ProtectionAction.AllowNodes => this with { ServicesFresh = false, NodeChangeTried = true },
        ProtectionAction.StoreIntent or ProtectionAction.KeepIntent => this with { IntentStored = true },
        _ => this,
    };
}

// The ordering rules between the node block and the Bluetooth service state. Pure: the caller runs each
// action, refreshes the facts, records the action in the progress and asks again until Done.
//
// Nothing documents how BluetoothSetServiceState behaves while the device's nodes are disabled, and the call
// installs or removes the service's driver, which creates or removes the per-service node and its children.
// So a service state is only ever changed while every target node is enabled:
//   Block with protection on   turn Handsfree off first while the nodes are enabled, read the services
//                              again (the Handsfree node and its children may have gone), then block.
//   Allow                      enable the nodes first, then turn Handsfree off again if it came back.
//   SetProtection while blocked  have the gate keep the wanted state (StoreIntent); while the nodes are
//                                unknown, keep it in the caller (KeepIntent). The next allow applies it.
//   Reverify                   after a connect or a boot, turn Handsfree off again only if it came back.
// Allow and Reverify only turn protection back on; they turn it off only for a kept intent.
// An Unknown service read never starts an automatic change.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal static class ProtectionPolicy
{
    public static NodePhase PhaseOf(BlockState state) => state switch
    {
        BlockState.Allowed => NodePhase.Allowed,
        BlockState.Blocked => NodePhase.Blocked,
        BlockState.Mixed => NodePhase.Mixed,
        _ => NodePhase.Unknown,
    };

    // A service state may change only while every target node is known to be enabled.
    public static bool MayChangeServices(NodePhase nodes) => nodes == NodePhase.Allowed;

    // Actions that change a device, which the caller treats as an operation in flight (driver churn from
    // them must not look like a disconnect). StoreIntent counts: it runs the protect verb, and the gate applies
    // the change there and then if the nodes were allowed in the meantime.
    public static bool IsDeviceChange(ProtectionAction action) =>
        action is ProtectionAction.ProtectOn or ProtectionAction.ProtectOff or ProtectionAction.BlockNodes or ProtectionAction.AllowNodes
            or ProtectionAction.StoreIntent;

    public static bool IsSatisfied(bool protect, AudioProtectionState services) =>
        protect ? services == AudioProtectionState.Protected : services == AudioProtectionState.NotProtected;

    public static ProtectionAction Next(ProtectionGoal goal, ProtectionFacts facts, ProtectionProgress progress)
    {
        ArgumentNullException.ThrowIfNull(facts);
        switch (goal)
        {
            case ProtectionGoal.Block:
                return NextForBlock(facts, progress);

            case ProtectionGoal.Allow:
                if (facts.Nodes != NodePhase.Allowed)
                {
                    return progress.NodeChangeTried ? ProtectionAction.Done : ProtectionAction.AllowNodes;
                }

                return NextServiceChange(facts, progress, bothWays: facts.IntentPending, whenUnknown: facts.IntentPending);

            case ProtectionGoal.Reverify:
                return MayChangeServices(facts.Nodes)
                    ? NextServiceChange(facts, progress, bothWays: facts.IntentPending, whenUnknown: false)
                    : ProtectionAction.Done;

            case ProtectionGoal.SetProtection:
                if (!MayChangeServices(facts.Nodes))
                {
                    return progress.IntentStored ? ProtectionAction.Done
                        : facts.Nodes == NodePhase.Unknown ? ProtectionAction.KeepIntent
                        : ProtectionAction.StoreIntent;
                }

                return NextServiceChange(facts, progress, bothWays: true, whenUnknown: true);

            default:
                throw new ArgumentOutOfRangeException(nameof(goal), goal, "Not a protection goal.");
        }
    }

    private static ProtectionAction NextForBlock(ProtectionFacts facts, ProtectionProgress progress)
    {
        if (progress.NodeChangeTried || facts.Nodes == NodePhase.Blocked)
        {
            return ProtectionAction.Done;
        }

        if (facts.Protect && MayChangeServices(facts.Nodes))
        {
            if (!progress.ServiceChangeTried)
            {
                if (!progress.ServicesFresh)
                {
                    return ProtectionAction.ReadServices;
                }

                if (facts.Services is AudioProtectionState.NotProtected or AudioProtectionState.Partial)
                {
                    return ProtectionAction.ProtectOn;
                }
            }
            else if (!progress.ServicesFresh)
            {
                return ProtectionAction.ReadServices;
            }
        }

        // A failed or unknown protection never holds up the block: the block is what keeps the AirPods on the
        // phone after a power cycle.
        return ProtectionAction.BlockNodes;
    }

    // bothWays: also turn protection off when that is the intent. whenUnknown: act on an Unknown read.
    private static ProtectionAction NextServiceChange(ProtectionFacts facts, ProtectionProgress progress, bool bothWays, bool whenUnknown)
    {
        if (!facts.Protect && !bothWays)
        {
            return ProtectionAction.Done;
        }

        if (!progress.ServicesFresh)
        {
            return ProtectionAction.ReadServices;
        }

        if (progress.ServiceChangeTried || IsSatisfied(facts.Protect, facts.Services))
        {
            return ProtectionAction.Done;
        }

        if (facts.Services == AudioProtectionState.Unknown && !whenUnknown)
        {
            return ProtectionAction.Done;
        }

        return facts.Protect ? ProtectionAction.ProtectOn : ProtectionAction.ProtectOff;
    }
}
