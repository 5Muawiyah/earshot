using System.Drawing;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// While a check runs the Updates page has no action button: the turning arc sits where the button would. The status title and the
// caption under it ("Checking for updates", the installed version) take the room left of the tile, and so must stop short of the arc,
// at every display scale and text size, however many lines the words wrap to.
[TestClass]
public sealed class UpdatesCheckingSpinnerTests
{
    private static readonly int[] Dpis = [96, 120, 144, 192];
    private static readonly double[] Scales = [1.0, 1.25, 1.5, 2.0, 2.25];

    [TestMethod]
    public void TheCheckingCaptionAndTitleStopShortOfTheSpinnerAtEveryScaleAndTextSize()
    {
        Phase5.CardSta.Run(() =>
        {
            var overlaps = new List<string>();
            foreach (int dpi in Dpis)
            {
                foreach (double scale in Scales)
                {
                    using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, scale, false);
                    card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.Checking), autoCheck: true, showRepair: true, version: "1.4.0"), dpi);
                    WidgetCardLayout.SetupLayout layout = card.CurrentSetupLayout!;
                    string where = "dpi " + dpi + ", text " + scale + ": ";
                    Assert.IsTrue(layout.Action.IsEmpty, where + "no action while checking");
                    Assert.IsFalse(layout.StatusIcon.IsEmpty, where + "the spinner has a place");
                    if (layout.StatusCaption.IntersectsWith(layout.StatusIcon))
                    {
                        overlaps.Add(where + "the caption " + layout.StatusCaption + " runs under the spinner " + layout.StatusIcon);
                    }

                    if (layout.StatusTitle.IntersectsWith(layout.StatusIcon))
                    {
                        overlaps.Add(where + "the title " + layout.StatusTitle + " runs under the spinner " + layout.StatusIcon);
                    }
                }
            }

            Assert.IsEmpty(overlaps, string.Join("\n", overlaps.Take(40)));
        });
    }
}
