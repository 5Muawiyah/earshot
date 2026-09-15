using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;

namespace Earshot.Boot.Gate;

// The audio protection part of the gate: gate protect-on and protect-off (run by \Earshot\Protect, whose
// longer time limit leaves room for BluetoothSetServiceState to install or remove drivers) and the
// protection restore that uninstall runs. See ProtectionGateRunner for the procedure.
//
// Without a Bluetooth service API for the context's node API the hooks leave ctx.Handled false, so the verbs
// report not available rather than reach a real service call from a test's fake node table.
internal sealed partial class GateActions
{
    static partial void RunProtectVerb(GateRunContext ctx, bool protect)
    {
        IBluetoothServiceApi? services = GateBluetooth.For(ctx.Nodes);
        if (services is not null)
        {
            new ProtectionGateRunner(services).Run(ctx, protect);
        }
    }

    static partial void RunProtectionRestore(GateRunContext ctx)
    {
        IBluetoothServiceApi? services = GateBluetooth.For(ctx.Nodes);
        if (services is not null)
        {
            new ProtectionGateRunner(services).Restore(ctx);
        }
    }
}
