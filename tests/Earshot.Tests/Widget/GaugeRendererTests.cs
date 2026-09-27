using System.Drawing;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// GaugeRenderer pixel checks.
[TestClass]
public sealed class GaugeRendererTests
{
    private const string FontFamily = "Segoe UI";

    private static WidgetSnapshot Snapshot(AirPodsWhere where, PartReading left, PartReading right) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with { Where = where, Left = left, Right = right };

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void BitmapSizeMatchesWidthForAndTheRequestedHeight(int dpi)
    {
        using Bitmap bitmap = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), dpi, 48, Color.White, hover: false, FontFamily);
        Assert.AreEqual(GaugeRenderer.WidthFor(dpi), bitmap.Width);
        Assert.AreEqual(48, bitmap.Height);
    }

    [TestMethod]
    public void NoBarWhenBothPartsAreUnknown()
    {
        using Bitmap bitmap = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        (Rectangle bar, _) = GaugeRenderer.ContentBounds(96, 48);
        Color pixel = bitmap.GetPixel(bar.X + (bar.Width / 2), bar.Y + (bar.Height / 2));
        Assert.AreEqual(GaugeRenderer.IdlePillAlpha, pixel.A, "Nothing beyond the hover pill should be drawn over the bar area.");
    }

    [TestMethod]
    public void ABarIsDrawnWhenAPercentIsKnown()
    {
        using Bitmap bitmap = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, new PartReading(70, false, null), PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        (Rectangle bar, _) = GaugeRenderer.ContentBounds(96, 48);
        Color pixel = bitmap.GetPixel(bar.X + 1, bar.Y + (bar.Height / 2));
        Assert.IsGreaterThan((int)GaugeRenderer.IdlePillAlpha, (int)pixel.A);
    }

    [TestMethod]
    public void BoltPixelsAppearOnlyWhenTheLowerBudIsCharging()
    {
        using Bitmap charging = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, new PartReading(50, true, null), PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        using Bitmap notCharging = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, new PartReading(50, false, null), PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        (Rectangle bar, _) = GaugeRenderer.ContentBounds(96, 48);
        int boltCentreX = bar.Right + (CardPlacement.Scale(GaugeRenderer.BoltWidthAt96, 96) / 2);
        int boltCentreY = (48 / 2) + 2;
        Color withBolt = charging.GetPixel(boltCentreX, boltCentreY);
        Color withoutBolt = notCharging.GetPixel(boltCentreX, boltCentreY);
        Assert.IsGreaterThan((int)withoutBolt.A, (int)withBolt.A);
    }

    [TestMethod]
    public void ElsewhereDimsTheMarkToAtMostFortyPercentOfFullInk()
    {
        using Bitmap full = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        using Bitmap dimmed = GaugeRenderer.Render(Snapshot(AirPodsWhere.Elsewhere, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);

        // (13, 19): inside the left earbud head at 20 px (pad 8 + markTop 14, offset into the mark).
        Color fullPixel = full.GetPixel(13, 19);
        Color dimmedPixel = dimmed.GetPixel(13, 19);
        Assert.IsGreaterThan(200, (int)fullPixel.A, "Sanity: the sample pixel must be solidly inside the mark.");
        Assert.IsLessThanOrEqualTo((int)Math.Round((fullPixel.A * 0.4) + 1), (int)dimmedPixel.A);
    }

    [TestMethod]
    public void HoverPillIsNeverZeroInsideAndExactlyZeroOutside()
    {
        using Bitmap bitmap = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        Color outside = bitmap.GetPixel(0, 0);
        Color inside = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        Assert.AreEqual(0, outside.A, "Outside the pill, nothing is painted: a click there must reach the taskbar.");
        Assert.AreNotEqual(0, inside.A, "Inside the pill, the alpha is never zero, so the whole gauge is one clickable target.");
    }

    [TestMethod]
    public void HoveringRaisesThePillInkToTwelvePercent()
    {
        using Bitmap idle = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: false, FontFamily);
        using Bitmap hovered = GaugeRenderer.Render(Snapshot(AirPodsWhere.ThisPc, PartReading.Unknown, PartReading.Unknown), 96, 48, Color.White, hover: true, FontFamily);
        Color idlePixel = idle.GetPixel(idle.Width / 2, idle.Height / 2);
        Color hoveredPixel = hovered.GetPixel(hovered.Width / 2, hovered.Height / 2);
        Assert.AreEqual(GaugeRenderer.IdlePillAlpha, idlePixel.A);
        Assert.IsGreaterThan((int)idlePixel.A, (int)hoveredPixel.A);
    }
}
