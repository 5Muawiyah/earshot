using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// An auto-hidden taskbar that has slid off screen takes the gauge with it. That is not a read that may be wrong, as a failed or
// crowded read can be, so the gauge goes at once, as it does for a full-screen window, and is not kept on screen for the
// five seconds a doubtful read is held.
[TestClass]
public sealed class GaugeAutoHideTests
{
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static TaskbarLayout Bar(int top, bool autoHide = true) =>
        new(0, new Rectangle(0, top, 1920, 48), TaskbarEdge.Bottom, autoHide, Monitor,
            [Start with { Y = top }, new Rectangle(807, top, 44, 48), new Rectangle(1678, top, 242, 48)], Start with { Y = top },
            96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, top, 242, 48));

    private static TaskbarLayout SlidIn() => Bar(1032);

    private static TaskbarLayout SlidAway() => Bar(1076);

    private sealed class Rig
    {
        public FakeGaugeSurface Surface { get; } = new();

        public FakeTrayIcon Icon { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public GaugeController Controller { get; }

        public Rig()
        {
            Controller = new GaugeController(() => Surface, Icon, () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false), Log, Time, null);
        }

        public void Layout(TaskbarLayout layout) => Controller.OnLayout(ITaskbarReader.Result.Ok(layout));
    }

    [TestMethod]
    public void ABarThatHasSlidAwayHidesTheGaugeOnTheReadThatSeesIt()
    {
        var rig = new Rig();
        rig.Layout(SlidIn());
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State);

        rig.Layout(SlidAway());

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoFreeSpace), rig.Controller.State, "One read of a bar that is gone is enough; no five seconds' wait.");
        CollectionAssert.Contains(rig.Surface.Calls, "HideWindow");
        Assert.IsTrue(rig.Icon.Visible, "The tray icon is back.");
    }

    [TestMethod]
    public void ABarThatSlidAwayDuringAPendingDoubtfulHideHidesAtOnce()
    {
        var rig = new Rig();
        rig.Layout(SlidIn());
        rig.Layout(SlidIn() with { Taskbar = new Rectangle(0, 1032, 60, 48), AutoHide = false });
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "A crowded read alone is held, as before.");

        rig.Layout(SlidAway());

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoFreeSpace), rig.Controller.State);
    }

    [TestMethod]
    public void ADoubtfulNoRoomReadIsStillHeldForItsWindow()
    {
        var rig = new Rig();
        rig.Layout(SlidIn() with { AutoHide = false });
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State);

        rig.Layout(SlidIn() with { Taskbar = new Rectangle(0, 1032, 60, 48), AutoHide = false });

        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "A bar too narrow for the gauge is a read that may be wrong; it waits to be confirmed.");
    }

    [TestMethod]
    public void TheGaugeComesBackWhenTheBarSlidesInAndStaysThere()
    {
        var rig = new Rig();
        rig.Layout(SlidIn());
        rig.Layout(SlidAway());
        Assert.IsInstanceOfType<GaugeState.Hidden>(rig.Controller.State);

        rig.Layout(SlidIn());
        Assert.IsInstanceOfType<GaugeState.Hidden>(rig.Controller.State, "One read of room is not enough to show it again.");
        rig.Time.Advance(GaugeStability.RoomBackWindow);
        rig.Layout(SlidIn());

        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State);
    }

    [TestMethod]
    public void ABarThatIsAlreadyAwayWhenTheGaugeIsNotShownJustStaysHidden()
    {
        var rig = new Rig();

        rig.Layout(SlidAway());

        Assert.AreEqual(new GaugeState.Hidden(HiddenReason.NoFreeSpace), rig.Controller.State);
        Assert.IsFalse(rig.Surface.Calls.Contains("Show"), string.Join(",", rig.Surface.Calls));
    }
}
