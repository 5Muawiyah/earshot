using Earshot.AudioProtection;
using Earshot.Infra;

namespace Earshot.Composition;

// Wires Protect audio quality. Building the controller starts nothing: its worker thread starts on the first
// change. Status reads are non-elevated; every change goes through the \Earshot\Protect task. In safe mode the
// registry wraps it, so only its status read runs.
internal static partial class CompositionRoot
{
    static partial void ConfigureProtection(ServiceRegistry r)
    {
        r.Protection = AudioProtectionController.Create(r.Log, r.Settings, Paths.Current);
    }
}
