using System.Drawing;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WidgetCardLayout pure row layout.
[TestClass]
public sealed class WidgetCardLayoutTests
{
    [TestMethod]
    public void WidthIs320At96Dpi()
    {
        Assert.AreEqual(320, WidgetCardLayout.WidthFor(96));
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    public void WidthScalesWithDpi(int dpi)
    {
        Assert.AreEqual(CardPlacement.Scale(320, dpi), WidgetCardLayout.WidthFor(dpi));
    }

    [TestMethod]
    public void ThreeColumnsAreLaidOutSideBySideWithoutOverlap()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
        Assert.IsTrue(layout.Left.Glyph.Right <= layout.Right.Glyph.Left, "Left column must not overlap the right column.");
        Assert.IsTrue(layout.Right.Glyph.Right <= layout.Case.Glyph.Left, "Right column must not overlap the case column.");
        Assert.IsTrue(layout.Left.Glyph.Top < layout.Left.Bar.Top, "The bar sits below the glyph.");
        Assert.IsTrue(layout.Left.Bar.Top < layout.Left.Percent.Top, "The percent text sits below the bar.");
    }

    [TestMethod]
    public void TheSwitchRowIsAbsentWhenAutoPauseIsUnsupported()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
        Assert.IsFalse(layout.ShowSwitch);
        Assert.AreEqual(Rectangle.Empty, layout.Switch);
    }

    [TestMethod]
    public void TheSwitchRowAppearsBelowTheButtonWhenAutoPauseIsSupported()
    {
        WidgetCardLayout.Layout with_ = WidgetCardLayout.Compute(96, showSwitch: true);
        Assert.IsTrue(with_.ShowSwitch);
        Assert.IsTrue(with_.Switch.Top >= with_.Button.Bottom);

        WidgetCardLayout.Layout without = WidgetCardLayout.Compute(96, showSwitch: false);
        Assert.IsGreaterThan(without.Height, with_.Height);
    }

    [TestMethod]
    public void CopyPerAirPodsWhere()
    {
        Assert.AreEqual("Not seen yet", WidgetCopy.Where(AirPodsWhere.Unknown, ""));
        Assert.AreEqual("On this PC", WidgetCopy.Where(AirPodsWhere.ThisPc, ""));
        Assert.AreEqual("On another device", WidgetCopy.Where(AirPodsWhere.Elsewhere, ""));
        Assert.AreEqual("On your Jonathan's iPhone", WidgetCopy.Where(AirPodsWhere.Elsewhere, "Jonathan's iPhone"));
        Assert.AreEqual("Not in use", WidgetCopy.Where(AirPodsWhere.NotInUse, ""));
    }

    [TestMethod]
    public void CopyPerBatteryReadAtAge()
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual("Battery not read yet", WidgetCopy.BatteryReadLine(null, now));
        Assert.AreEqual("Battery read 30 s ago", WidgetCopy.BatteryReadLine(now - TimeSpan.FromSeconds(30), now));
        Assert.AreEqual("Battery read 5 min ago", WidgetCopy.BatteryReadLine(now - TimeSpan.FromMinutes(5), now));
        Assert.AreEqual("Battery read 2 h ago", WidgetCopy.BatteryReadLine(now - TimeSpan.FromHours(2), now));
    }

    [TestMethod]
    public void AnUnknownPartShowsNoReadingNeverANumberOrADash()
    {
        Assert.AreEqual("No reading", WidgetCopy.Percent(null));
        Assert.AreEqual("70%", WidgetCopy.Percent(70));
    }
}
