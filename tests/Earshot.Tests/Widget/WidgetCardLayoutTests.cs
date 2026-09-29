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
    public void TheColumnLabelSitsAboveTheGlyph()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
        Assert.IsTrue(layout.Left.Label.Bottom <= layout.Left.Glyph.Top, "The L/R/Case label sits above the glyph, matching the mockup.");
        Assert.IsTrue(layout.Right.Label.Bottom <= layout.Right.Glyph.Top);
        Assert.IsTrue(layout.Case.Label.Bottom <= layout.Case.Glyph.Top);
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
    public void TheSetupButtonIsAbsentByDefault()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);
        Assert.IsFalse(layout.ShowSetupButton);
        Assert.AreEqual(Rectangle.Empty, layout.SetupButton);
        Assert.IsTrue(layout.ShowColumns);
    }

    // No reading: the grid is replaced by one 32 px primary button, 16 px below the read line, and the three
    // columns are not laid out at all.
    [TestMethod]
    public void TheSetupButtonReplacesTheGridSixteenPixelsBelowTheReadLine()
    {
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(dpi, showSwitch: false, showSetupButton: true);

            Assert.IsTrue(layout.ShowSetupButton);
            Assert.IsFalse(layout.ShowColumns, "The grid gives way to the button at " + dpi + " DPI.");
            Assert.AreEqual(Earshot.Popup.CardPlacement.Scale(32, dpi), layout.SetupButton.Height, "A 32 px button at " + dpi + " DPI.");
            Assert.AreEqual(layout.ReadLine.Bottom + Earshot.Popup.CardPlacement.Scale(16, dpi), layout.SetupButton.Top, "16 px above at " + dpi + " DPI.");
            Assert.IsTrue(layout.Button.Top >= layout.SetupButton.Bottom, "The Connect button stays below it.");
            Assert.AreEqual(Rectangle.Empty, layout.Left.Label);
        }

        WidgetCardLayout.Layout withGrid = WidgetCardLayout.Compute(96, showSwitch: false);
        WidgetCardLayout.Layout withButton = WidgetCardLayout.Compute(96, showSwitch: false, showSetupButton: true);
        Assert.IsLessThan(withGrid.Height, withButton.Height, "The card is shorter without the grid.");
    }

    [TestMethod]
    public void TheSetupButtonTakesItsMeasuredWidthAndAtMostTheContentWidth()
    {
        Assert.AreEqual(100, WidgetCardLayout.Compute(96, showSwitch: false, showSetupButton: true, setupButtonWidth: 100).SetupButton.Width);
        int content = WidgetCardLayout.Compute(96, showSwitch: false, showSetupButton: true).SetupButton.Width;
        Assert.AreEqual(WidgetCardLayout.WidthAt96 - (2 * WidgetCardLayout.PaddingAt96), content, "No measured width: the full content width.");
        Assert.AreEqual(content, WidgetCardLayout.Compute(96, showSwitch: false, showSetupButton: true, setupButtonWidth: 5000).SetupButton.Width);
    }

    // The caption line after a set-up that could not read sits between the read line and the button.
    [TestMethod]
    public void TheSetupCaptionSitsAboveTheButton()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false, showSetupButton: true, showSetupCaption: true);

        Assert.IsTrue(layout.SetupCaption.Top >= layout.ReadLine.Bottom);
        Assert.IsTrue(layout.SetupButton.Top >= layout.SetupCaption.Bottom);
    }

    // ---- The set-up pages, on the sub-page frame, per DPI

    private static SetupViewModel PickPage() => SetupViewModel.Pick(BatterySetupPicks.Default);

    [TestMethod]
    public void TheFrameHasA48pxHeaderWithBackTitleAndStepAndA64pxFooter()
    {
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            int S(int v) => Earshot.Popup.CardPlacement.Scale(v, dpi);
            SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight: S(100), buttonCount: 2);

            Assert.AreEqual(S(360), frame.Width);
            Assert.AreEqual(S(48), frame.Header.Height, "Header 48 at " + dpi);
            Assert.AreEqual(new Size(S(32), S(32)), frame.Back.Size, "Back button 32 by 32 at " + dpi);
            Assert.AreEqual(S(8), frame.Back.Left, "8 px from the left at " + dpi);
            Assert.AreEqual(S(16), frame.Width - frame.Step.Right, "The step counter is 16 px from the right at " + dpi);
            Assert.IsTrue(frame.Title.Left >= frame.Back.Right, "The title starts after the back button.");
            Assert.IsTrue(frame.Title.Right <= frame.Step.Left, "The title ends before the step counter.");
            Assert.AreEqual(S(64), frame.Footer.Height, "Footer 64 at " + dpi);
            Assert.AreEqual(frame.Header.Bottom + S(100), frame.Footer.Top, "The body sits between them.");
            Assert.AreEqual(frame.Footer.Bottom, frame.Height);
            Assert.HasCount(2, frame.Buttons);
            Assert.AreEqual(S(8), frame.Buttons[1].Left - frame.Buttons[0].Right, "An 8 px gap at " + dpi);
            Assert.IsTrue(Math.Abs(frame.Buttons[0].Width - frame.Buttons[1].Width) <= 1, "Split 50/50 at " + dpi);
            Assert.AreEqual(S(32), frame.Buttons[0].Height);
            Assert.AreEqual(S(16), frame.Buttons[0].Left, "16 px footer padding at " + dpi);
            Assert.AreEqual(S(16), frame.Width - frame.Buttons[1].Right);
        }
    }

    [TestMethod]
    public void OneFooterButtonTakesTheWholeWidthAndNoButtonMeansNoFooter()
    {
        SubPageFrame.FrameLayout one = SubPageFrame.Compute(96, 100, 1);
        Assert.HasCount(1, one.Buttons);
        Assert.AreEqual(360 - 32, one.Buttons[0].Width);

        SubPageFrame.FrameLayout none = SubPageFrame.Compute(96, 100, 0);
        Assert.AreEqual(0, none.Footer.Height);
        Assert.AreEqual(48 + 100, none.Height);
    }

    // Three equal columns 8 apart, each 148 high with the value, both chevrons and the Charging row inside.
    [TestMethod]
    public void ThePickersAreThreeEqualColumnsEachOneHundredAndFortyEightHigh()
    {
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            int S(int v) => Earshot.Popup.CardPlacement.Scale(v, dpi);
            WidgetCardLayout.SetupLayout layout = WidgetCardLayout.Setup(PickPage(), dpi);

            Assert.HasCount(3, layout.Pickers);
            foreach (WidgetCardLayout.PickerLayout picker in layout.Pickers)
            {
                Assert.IsTrue(Math.Abs(S(148) - picker.Box.Height) <= 3, "148 high at " + dpi + " (each part is scaled on its own): " + picker.Box.Height);
                Assert.IsTrue(picker.Box.Contains(picker.Label) && picker.Box.Contains(picker.Up) && picker.Box.Contains(picker.Value)
                    && picker.Box.Contains(picker.Down) && picker.Box.Contains(picker.Toggle), "Everything sits inside the box.");
                Assert.AreEqual(new Size(S(40), S(24)), picker.Up.Size, "Chevron 40 by 24.");
                Assert.AreEqual(new Size(S(40), S(24)), picker.Down.Size);
                Assert.AreEqual(S(28), picker.Value.Height, "The value is 28 high.");
                Assert.AreEqual(new Size(S(40), S(20)), picker.Toggle.Size, "The toggle is 40 by 20.");
                Assert.IsTrue(picker.Up.Bottom <= picker.Value.Top && picker.Value.Bottom <= picker.Down.Top && picker.Down.Bottom <= picker.Toggle.Top,
                    "Label, up, value, down, then the Charging row.");
            }

            Assert.AreEqual(S(8), layout.Pickers[1].Box.Left - layout.Pickers[0].Box.Right, "An 8 px gap at " + dpi);
            Assert.AreEqual(S(8), layout.Pickers[2].Box.Left - layout.Pickers[1].Box.Right);
            Assert.AreEqual(S(16), layout.Pickers[0].Box.Left, "16 px side padding.");
            Assert.AreEqual(S(16), layout.Frame.Width - layout.Pickers[2].Box.Right);
            Assert.IsTrue(Math.Abs(layout.Pickers[0].Box.Width - layout.Pickers[1].Box.Width) <= 1, "Equal columns.");
        }
    }

    [TestMethod]
    public void ThePromptAndTheStatusRowFollowTheBodyRules()
    {
        SetupViewModel listening = SetupViewModel.Listening();
        WidgetCardLayout.SetupLayout layout = WidgetCardLayout.Setup(listening, 96);

        Assert.AreEqual(48 + 4, layout.Prompt.Top, "The body has 4 px of top padding under the 48 px header.");
        Assert.AreEqual(20, layout.Prompt.Height, "The prompt is 20 high.");
        Assert.AreEqual(16, layout.Prompt.Left);
        Assert.AreEqual(layout.Prompt.Bottom + 16, layout.StatusIcon.Top, "16 between items.");
        Assert.AreEqual(new Size(20, 20), layout.StatusIcon.Size, "A 20 px status icon.");
        Assert.AreEqual(layout.StatusIcon.Right + 12, layout.StatusText.Left, "12 between the icon and the text.");
        Assert.AreEqual(4 + 20 + 16 + 20 + 20, layout.Frame.Body.Height, "4 top, the prompt, 16 between, the status row, 20 bottom.");
    }

    [TestMethod]
    public void EverySetupPageLaysOutInsideItsFrame()
    {
        SetupViewModel[] pages =
        [
            SetupViewModel.Listening(), PickPage(),
            SetupViewModel.Done(BatterySetupResultStatus.BatterySetUp), SetupViewModel.Done(BatterySetupResultStatus.CaseSetUp),
            SetupViewModel.Done(BatterySetupResultStatus.CaseSetUpBudsSame), SetupViewModel.Done(BatterySetupResultStatus.CouldNotRead),
            SetupViewModel.Failed(BatterySetupListenStatus.NotFound), SetupViewModel.Failed(BatterySetupListenStatus.Ambiguous),
            SetupViewModel.Failed(BatterySetupListenStatus.WatcherNotStarted),
        ];
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            foreach (SetupViewModel page in pages)
            {
                WidgetCardLayout.SetupLayout layout = WidgetCardLayout.Setup(page, dpi);
                var body = layout.Frame.Body;
                foreach (Rectangle part in new[] { layout.Prompt, layout.Caption, layout.StatusIcon, layout.StatusText, layout.StatusSub })
                {
                    Assert.IsTrue(part.IsEmpty || body.Contains(part), "A part of '" + page.Status + page.Prompt + "' is outside the body at " + dpi);
                }

                Assert.HasCount(page.Buttons.Count, layout.Frame.Buttons);
            }
        }
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
