using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;

namespace Earshot.Boot.Gate;

// The audio protection part of the gate: gate protect-on and protect-off (run by \Earshot\Protect, whose
// longer time limit leaves room for BluetoothSetServiceState to install or remove drivers) and the
// protection restore that uninstall runs. See ProtectionGateRunner for the procedure.
//
// Without a Bluetooth service API in the context the hooks leave ctx.Handled false, so the verbs report not
// available. The context only ever pairs the real node API with the real Bluetooth API (GateRunContext).
internal sealed partial class GateActions
{
    static partial void RunProtectVerb(GateRunContext ctx, bool protect)
    {
        if (ctx.Bluetooth is { } services)
        {
            new ProtectionGateRunner(services).Run(ctx, protect);
        }
    }

    static partial void RunProtectionRestore(GateRunContext ctx)
    {
        if (ctx.Bluetooth is { } services)
        {
            new ProtectionGateRunner(services).Restore(ctx);
        }
    }
}
