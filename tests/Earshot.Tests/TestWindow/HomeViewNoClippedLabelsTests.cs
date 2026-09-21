using System.Drawing;
using System.Windows.Forms;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// the layout's acceptance bar for this commit's Home view: no label's preferred
// size (the size it would need to show all of its own text) exceeds its own actual bounds, at the
// window's default size. A label given a fixed size (or a MaximumSize) too small for its real text
// is exactly what "clipped" means; AutoEllipsis is the same defect wearing a different coat (it
// cuts the text with "..." instead of overflowing silently), so it is checked the same way here:
// TextRenderer.MeasureText ignores AutoEllipsis and always reports the space the full text needs.
[TestClass]
public sealed class HomeViewNoClippedLabelsTests
{
    [TestMethod]
    public void NoLabelOnHomeIsClippedAtTheDefaultWindowSize()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            System.Windows.Forms.Application.DoEvents();

            var offenders = new List<string>();
            CollectClippedLabels(form.HomePanelForTests, offenders);

            Assert.IsEmpty(offenders, "Clipped label(s) on Home:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        });
    }

    // Recurses the whole Home view's control tree (labels can sit inside a FlowLayoutPanel nested
    // inside another, such as the banner's own how-to row, or the collapsed More section), and
    // checks every Label that currently has real text and real bounds (Width/Height > 0): a
    // collapsed section's own controls still have a Width/Height from their last layout pass even
    // while Visible is false, so a genuinely too-small label inside More is still caught here, not
    // only once an owner has opened it.
    private static void CollectClippedLabels(Control root, List<string> offenders)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Label label && !string.IsNullOrEmpty(label.Text) && label.Width > 0 && label.Height > 0)
            {
                // Label.GetPreferredSize is the exact calculation WinForms itself uses to size an
                // AutoSize label (and the one TextRenderer.MeasureText's own default flags, without
                // TextBoxControl's extra edit-control padding, agree with): asking it for the size
                // needed at this label's own current width is "how tall would this need to be to
                // show all of its text here", the direct definition of not clipped.
                Size needed = label.GetPreferredSize(new Size(label.Width, 0));

                // A couple of pixels' slack for rounding, which is not itself a clip.
                if (needed.Width > label.Width + 4 || needed.Height > label.Height + 4)
                {
                    string shortText = label.Text.Length > 40 ? string.Concat(label.Text.AsSpan(0, 40), "...") : label.Text;
                    offenders.Add(
                        (string.IsNullOrEmpty(label.Name) ? label.GetType().Name : label.Name) + " (\"" + shortText + "\"): needs " +
                        needed + " but has " + label.Size);
                }
            }

            CollectClippedLabels(child, offenders);
        }
    }
}
