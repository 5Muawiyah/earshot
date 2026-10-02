using System.Drawing;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The design's Updates page, from the pure layout: a header, a status row (a 40 by 40 accent tile, the title, the caption and
// the action), then Check automatically, What's new and Repair, each a settings-style row on a surface. Repair is here, not on the
// settings page. Pure: no window, no drawing.
[TestClass]
public sealed class UpdatesPageDesignLayoutTests
{
    private static SetupViewModel Page(UpdateStage stage, bool repair = true, int? percent = null) =>
        Earshot.App.WidgetCardUpdatePage.From(UpdateViewModel.For(stage, new ReleaseVersion(1, 3, 0), new ReleaseVersion(1, 4, 0), percent, reason: null, notice: null))
        with { Rows = new UpdatesRows(AutoCheck: false, ShowRepair: repair, Version: "1.4.0") };

    private static WidgetCardLayout.SetupLayout Layout(SetupViewModel page, int dpi = 96, double t = 1.0, int subLines = 1, int actionWidth = 0) =>
        WidgetCardLayout.Setup(page, dpi, 1, 1, subLines, t, actionWidth);

    [TestMethod]
    public void TheStatusRowIsA40By40TileATitleACaptionAndTheActionOnOneSurface()
    {
        SetupViewModel page = Page(UpdateStage.UpToDate);
        WidgetCardLayout.SetupLayout l = Layout(page, actionWidth: 80);

        Assert.AreEqual(new Size(40, 40), l.Tile.Size);
        Assert.AreEqual(12, l.StatusSurface.X);
        Assert.AreEqual(336, l.StatusSurface.Width);
        Assert.AreEqual(56, l.StatusSurface.Height, "A 40 tile with 8 above and below.");
        Assert.IsTrue(l.StatusSurface.Contains(l.Tile));
        Assert.AreEqual(1, page.Buttons.Count, "Up to date offers Check.");
        Assert.IsFalse(l.Action.IsEmpty, "The one action is in the status row, not the footer.");
        Assert.AreEqual(80, l.Action.Width);
        Assert.AreEqual(32, l.Action.Height, "A button is 32 high.");
        Assert.IsEmpty(l.Frame.Buttons, "No footer.");
        Assert.IsGreaterThanOrEqualTo(l.Tile.Right, l.StatusTitle.Left);
        Assert.IsLessThanOrEqualTo(l.Action.Left, l.StatusTitle.Right);
        Assert.IsGreaterThanOrEqualTo(l.StatusTitle.Bottom, l.StatusCaption.Top, "The caption is under the title.");
    }

    [TestMethod]
    public void TheRowsAreCheckAutomaticallyWhatsNewAndRepairInThatOrderAndRepairOnlyWithAnInstall()
    {
        CollectionAssert.AreEqual(Three, Layout(Page(UpdateStage.UpToDate, repair: true)).UpdateRows.Select(r => r.Index).ToArray());
        CollectionAssert.AreEqual(Two, Layout(Page(UpdateStage.UpToDate, repair: false)).UpdateRows.Select(r => r.Index).ToArray());
    }

    private static readonly int[] Three = [0, 1, 2];

    private static readonly int[] Two = [0, 1];

    // Each row is as high as a single-line settings row (48 at 100%, 58 at 150% text), 336 wide, 4 apart, with a 40 by 20 toggle.
    [TestMethod]
    [DataRow(1.0, 48)]
    [DataRow(1.5, 58)]
    public void RowsAreSingleLineSettingsRowsOnSurfaces(double t, int height)
    {
        WidgetCardLayout.SetupLayout l = Layout(Page(UpdateStage.UpToDate), t: t);
        Rectangle previous = l.StatusSurface;
        foreach (WidgetCardLayout.UpdatesRowLayout row in l.UpdateRows)
        {
            Assert.AreEqual(height, row.Surface.Height, "Row " + row.Index);
            Assert.AreEqual(336, row.Surface.Width);
            Assert.AreEqual(previous.Bottom + 4, row.Surface.Top, "Rows are 4 apart.");
            Assert.IsTrue(row.Surface.Contains(row.Control));
            Assert.AreEqual(12 + 14, row.Icon.X, "The icon is 14 into the surface.");
            Assert.AreEqual(row.Surface.Right - 12, row.Control.Right, "Controls end 12 short of the right edge.");
            previous = row.Surface;
        }

        Assert.AreEqual(new Size(40, 20), l.UpdateRows[0].Control.Size, "The toggle.");
        Assert.AreEqual(WidgetCardLayout.RowHeight(96, t), l.UpdateRows[2].Control.Height, "Repair is a button, 20t + 12 high.");
    }

    [TestMethod]
    public void ADownloadShowsItsProgressUnderTheStatusAndTheRowsFollow()
    {
        SetupViewModel page = Page(UpdateStage.Downloading, percent: 60);
        WidgetCardLayout.SetupLayout l = Layout(page);

        Assert.IsTrue(page.ShowProgress);
        Assert.IsFalse(l.Progress.IsEmpty);
        Assert.IsGreaterThanOrEqualTo(l.StatusSurface.Bottom, l.Progress.Top);
        Assert.IsGreaterThanOrEqualTo(l.Progress.Bottom, l.UpdateRows[0].Surface.Top);
        Assert.AreEqual(page.Buttons.Count == 1, !l.Action.IsEmpty, "The design shows no button while downloading; the flow's Cancel is kept, so one button sits in the status row.");
    }

    [TestMethod]
    public void AllStatesFitAtEveryScaleAndTextSize()
    {
        foreach (UpdateStage stage in new[] { UpdateStage.UpToDate, UpdateStage.Available, UpdateStage.Downloading, UpdateStage.CheckFailed })
        {
            foreach ((int dpi, double t) in new[] { (96, 1.0), (96, 1.5), (144, 1.0), (144, 2.25) })
            {
                WidgetCardLayout.SetupLayout l = Layout(Page(stage, percent: stage == UpdateStage.Downloading ? 60 : null), dpi, t, subLines: 2);
                int previousBottom = l.StatusSurface.Bottom;
                foreach (WidgetCardLayout.UpdatesRowLayout row in l.UpdateRows)
                {
                    Assert.IsGreaterThanOrEqualTo(previousBottom, row.Surface.Top, stage + " at " + dpi + "/" + t);
                    previousBottom = row.Surface.Bottom;
                }

                Assert.IsLessThanOrEqualTo(l.Frame.Height, previousBottom, stage + " at " + dpi + "/" + t);
                Assert.AreEqual(Earshot.Popup.CardPlacement.Scale(360, dpi), l.Frame.Width);
            }
        }
    }
}
