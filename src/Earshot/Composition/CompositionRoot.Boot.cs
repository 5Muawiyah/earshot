using Earshot.Boot;
using Earshot.Infra;

namespace Earshot.Composition;

// Wires the boot block. Building the controller starts nothing: the system worker's thread starts on the
// first request. In safe mode the registry wraps it, so only its read-only members run.
//
// The worker is made here and kept on the registry, so the protection hook (which runs after this one) puts
// its own requests on the same thread: a block or allow and a protect-on or protect-off started from this
// tray then queue behind each other rather than running side by side, which design D requires. Read-only
// status calls do not use it, so they never wait behind a change.
internal static partial class CompositionRoot
{
    static partial void ConfigureBoot(ServiceRegistry r)
    {
        var worker = new SystemWorker(r.Log);
        r.SystemWorker = worker;
        r.Block = BlockController.Create(r.Log, r.Settings, Paths.Current, worker);
    }
}
