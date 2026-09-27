using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.EarPause;

namespace Earshot.Composition;

// Builds the widget's data pipeline and wires it into the registry, gated by WidgetSettings.Enabled: off
// (the default is on, but an owner can turn it off), nothing is built, IWidgetStatus and MediaSessions
// stay null, and no WinRT type, source or claim file is touched.
//
// Unlike every other Configure* hook, this one is not called from CompositionRoot.Build: the status
// service needs a BootBlockStatus reader, and the block coordinator that provides one is built after the
// registry, from the registry itself. TrayContext calls BuildWidget from its own constructor, once the
// coordinator exists, and owns starting, suspending, resuming and closing the result.
//
// Auto-pause's live wiring (feeding AutoPause.ApplyAsync from the ownership verdict and the in-ear bits
// the status service reads internally) is not built here: IWidgetStatus's public surface carries only the
// published snapshot, not the ownership verdict or the render container ids ApplyAsync needs, so that
// wiring belongs inside the status service's own pipeline, not composition. Recorded in the report as
// unfinished, not invented.
internal static partial class CompositionRoot
{
    internal static IWidgetStatus? BuildWidget(ServiceRegistry r, Func<BootBlockStatus?> blockStatus, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(blockStatus);
        ArgumentNullException.ThrowIfNull(time);

        if (!r.Settings.Current.Widget.Enabled)
        {
            return null;
        }

        // The setter already wraps this in safe mode (ServiceRegistry.MediaSessions), the same way
        // Connection, Block and Protection are wrapped on assignment.
        r.MediaSessions = new WindowsMediaSessions(r.Log);

        var claimStore = new ClaimStore(Paths.Current.WidgetClaimFile, r.Log);
        var status = new WidgetStatusService(
            static () => new WinRtAdvertisementSource(),
            claimStore,
            r.Settings,
            r.Monitor,
            blockStatus,
            r.Log,
            r.UiPost,
            time,
            static () => ProximityDecodeTable.Current);
        r.WidgetStatus = status;
        return status;
    }
}
