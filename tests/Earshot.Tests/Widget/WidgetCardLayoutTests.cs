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
    public void WidthIs360At96DpiLikeEveryOtherView()
    {
        Assert.AreEqual(360, WidgetCardLayout.WidthFor(96));
        Assert.AreEqual(360, WidgetCardLayout.Compute(96, showSwitch: false).Width);
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    public void WidthScalesWithDpi(int dpi)
    {
        Assert.AreEqual(CardPlacement.Scale(360, dpi), WidgetCardLayout.WidthFor(dpi));
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

    // The three columns are always laid out: a part with no value says so in its own column.
    [TestMethod]
    public void TheColumnsAreAlwaysLaidOut()
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(96, showSwitch: false);

        Assert.IsTrue(layout.ShowColumns);
        Assert.IsFalse(layout.Left.Label.IsEmpty);
    }

    // ---- The sub-pages, on the sub-page frame, per DPI

    private static SetupViewModel Page(string? prompt, string? caption, string? status, string? sub, params SetupButton[] buttons) =>
        new("Updates", null, prompt, caption, SetupIcon.Spinner, status, sub, buttons, 0);

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

    [TestMethod]
    public void ThePromptAndTheStatusRowFollowTheBodyRules()
    {
        SetupViewModel page = Page("A prompt", null, "A status", null, new SetupButton("Cancel", false, SetupAction.Cancel));
        WidgetCardLayout.SetupLayout layout = WidgetCardLayout.Setup(page, 96);

        Assert.AreEqual(48 + 4, layout.Prompt.Top, "The body has 4 px of top padding under the 48 px header.");
        Assert.AreEqual(20, layout.Prompt.Height, "The prompt is 20 high.");
        Assert.AreEqual(16, layout.Prompt.Left);
        Assert.AreEqual(layout.Prompt.Bottom + 16, layout.StatusIcon.Top, "16 between items.");
        Assert.AreEqual(new Size(20, 20), layout.StatusIcon.Size, "A 20 px status icon.");
        Assert.AreEqual(layout.StatusIcon.Right + 12, layout.StatusText.Left, "12 between the icon and the text.");
        Assert.AreEqual(4 + 20 + 16 + 20 + 20, layout.Frame.Body.Height, "4 top, the prompt, 16 between, the status row, 20 bottom.");
    }

    [TestMethod]
    public void EverySubPageLaysOutInsideItsFrame()
    {
        var one = new SetupButton("Update", true, SetupAction.Update);
        var two = new SetupButton("Cancel", false, SetupAction.Cancel);
        SetupViewModel[] pages =
        [
            Page("A prompt", null, "A status", null, two),
            Page("A prompt", "A caption under it", "A status", "A line under the status", one, two),
            Page(null, null, "A status", "A line under the status", one),
            Page(null, "A caption on its own", "A status", null, one),
            Page(null, null, null, null),
            Page(null, null, "Downloading", null, two) with { ShowProgress = true, ProgressPercent = 40 },
        ];
        foreach (int dpi in new[] { 96, 120, 144 })
        {
            foreach (SetupViewModel page in pages)
            {
                WidgetCardLayout.SetupLayout layout = WidgetCardLayout.Setup(page, dpi);
                var body = layout.Frame.Body;
                foreach (Rectangle part in new[] { layout.Prompt, layout.Caption, layout.StatusIcon, layout.StatusText, layout.StatusSub, layout.Progress })
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
        Assert.AreEqual("", WidgetCopy.BatteryReadLine(now - TimeSpan.FromSeconds(30), now), "Fresh: no age, the line's place is kept (ReadLineStillnessTests).");
        Assert.AreEqual("Battery read under 1 min ago", WidgetCopy.BatteryReadLine(now - TimeSpan.FromSeconds(31), now));
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
