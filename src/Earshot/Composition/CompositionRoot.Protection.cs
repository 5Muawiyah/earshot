using Earshot.AudioProtection;
using Earshot.Infra;

namespace Earshot.Composition;

// Wires Protect audio quality. Building the controller starts nothing: its worker thread starts on the first
// change. Status reads are non-elevated; every change goes through the \Earshot\Protect task. In safe mode the
// registry wraps it, so only its status read runs.
//
// The boot hook runs first and leaves its system worker on the registry; protection changes go on that same
// thread, so a protect verb and a block or allow from this tray never run side by side. Without that hook
// (a build without the boot block) the controller makes a worker of its own and owns it.
internal static partial class CompositionRoot
{
    static partial void ConfigureProtection(ServiceRegistry r)
    {
        r.Protection = r.SystemWorker is { } shared
            ? AudioProtectionController.Create(r.Log, r.Settings, Paths.Current, shared)
            : AudioProtectionController.Create(r.Log, r.Settings, Paths.Current);
    }
}
