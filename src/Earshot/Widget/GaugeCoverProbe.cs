using Earshot.Interop;

namespace Earshot.Widget;

// What sits at the gauge's centre: the gauge itself, or another window and its root class. RootClassName is
// "" when no window is there. Class only, never a title.
internal readonly record struct GaugeCover(bool IsGauge, string RootClassName, bool BelongsToExplorer = false);

// What the controller needs to look at the gauge's own centre. WindowCoverProbe is the real one; tests
// supply a fake.
internal interface IGaugeCoverProbe
{
    GaugeCover Probe(ShownGauge gauge);
}

// WindowFromPoint at the gauge's centre, then the root of that window and its class. Cheap in-process reads,
// safe on the UI thread. Nothing is changed and nothing is sent to the other window.
internal sealed class WindowCoverProbe : IGaugeCoverProbe
{
    private const string ShellTrayWndClass = "Shell_TrayWnd";

    // Test seam only: counts real constructions so the real-surface guard can prove no test builds one by
    // accident. Never read or reset in production.
    internal static int ConstructionCount;

    public WindowCoverProbe()
    {
        Interlocked.Increment(ref ConstructionCount);
    }

    public GaugeCover Probe(ShownGauge gauge)
    {
        Point centre = new(gauge.Bounds.X + (gauge.Bounds.Width / 2), gauge.Bounds.Y + (gauge.Bounds.Height / 2));
        nint atCentre = NativeMethods.WindowFromPoint(new POINT { x = centre.X, y = centre.Y });
        if (atCentre == 0)
        {
            return new GaugeCover(IsGauge: false, RootClassName: "");
        }

        nint root = NativeMethods.GetAncestor(atCentre, NativeMethods.GA_ROOT);
        nint effective = root != 0 ? root : atCentre;
        if (atCentre == gauge.Handle || effective == gauge.Handle)
        {
            return new GaugeCover(IsGauge: true, RootClassName: "");
        }

        uint explorer = GaugeWindowIdentityReader.ProcessOf(NativeMethods.FindWindowW(ShellTrayWndClass, null));
        WindowIdentity? identity = GaugeWindowIdentityReader.Read(effective, explorer);
        return new GaugeCover(IsGauge: false, identity?.ClassName ?? "", identity?.BelongsToExplorer ?? false);
    }
}
