using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Cause 5 of the stutter: the gauge's push key held its tooltip, whose text carries the age of the reading, so the gauge was
// pushed through UpdateLayeredWindow again whenever that text moved, though no pixel had.
[TestClass]
public sealed class GaugePushKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static GaugeContent ContentAtAge(TimeSpan age) =>
        GaugeContent.From(
            WidgetSnapshot.Empty(WidgetWatcherState.Started) with
            {
                Where = AirPodsWhere.ThisPc,
                Selection = BroadcastSelectionState.Linked,
                Left = new PartReading(70, null, null) { ReadAt = Now - age },
                Right = new PartReading(60, null, null) { ReadAt = Now - age },
            },
            Now, GaugeDisplaySettings.Default);

    private static GaugePushKey Key(GaugeContent content) =>
        GaugePushKey.Of(content, GaugePalette.Create(true, Color.Blue, false, Color.Black), GaugeLayout.For(96), hover: false, "Segoe UI", new Point(900, 732));

    [TestMethod]
    public void ATooltipThatOnlyChangesItsAgeDoesNotMakeANewPush()
    {
        GaugeContent two = ContentAtAge(TimeSpan.FromMinutes(2));
        GaugeContent three = ContentAtAge(TimeSpan.FromMinutes(3));
        Assert.AreNotEqual(two.Tooltip, three.Tooltip, "Sanity: the tooltip text does move with the age.");

        Assert.AreEqual(Key(two), Key(three));
    }

    [TestMethod]
    public void AChangeThatIsDrawnStillMakesANewPush()
    {
        GaugeContent seventy = ContentAtAge(TimeSpan.FromMinutes(2));
        GaugeContent fresh = ContentAtAge(TimeSpan.FromSeconds(3));

        Assert.AreNotEqual(Key(seventy), Key(fresh), "Stale ink to live ink is a drawn change.");
        Assert.AreNotEqual(Key(seventy), GaugePushKey.Of(seventy, GaugePalette.Create(true, Color.Blue, false, Color.Black), GaugeLayout.For(96), hover: true, "Segoe UI", new Point(900, 732)));
        Assert.AreNotEqual(Key(seventy), GaugePushKey.Of(seventy, GaugePalette.Create(true, Color.Blue, false, Color.Black), GaugeLayout.For(96), hover: false, "Segoe UI", new Point(901, 732)));
    }
}
