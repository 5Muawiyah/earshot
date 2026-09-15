using Earshot.Boot;
using Earshot.Infra;

namespace Earshot.Composition;

// Wires the boot block. Building the controller starts nothing: its worker thread starts on the first
// status read or request. In safe mode the registry wraps it, so only its read-only members run.
internal static partial class CompositionRoot
{
    static partial void ConfigureBoot(ServiceRegistry r)
    {
        r.Block = BlockController.Create(r.Log, r.Settings, Paths.Current);
    }
}
