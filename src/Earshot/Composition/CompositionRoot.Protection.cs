using Earshot.AudioProtection;
using Earshot.Infra;

namespace Earshot.Composition;

// Wires Protect audio quality. Building the controller starts nothing: its worker thread starts on the first
// change. Status reads are non-elevated; every change goes through the \Earshot\Protect task. In safe mode the
// registry wraps it, so only its status read runs.
//
// The controller has a worker of its own: the block controller does not expose its worker, so a protection
// change and a block or allow from this tray do not yet queue behind each other. When one worker is shared,
// this becomes AudioProtectionController.Create(r.Log, r.Settings, Paths.Current, sharedWorker).
internal static partial class CompositionRoot
{
    static partial void ConfigureProtection(ServiceRegistry r)
    {
        r.Protection = AudioProtectionController.Create(r.Log, r.Settings, Paths.Current);
    }
}
