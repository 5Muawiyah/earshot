using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The coordinator's own reading of s6-05: "the state column is pushed off-screen by the two text
// columns. State must always be visible without scrolling at the default window size: put it
// second, size the columns to the window." A WinForms ListView's own column widths and order are
// not something a headless test can observe by running the window (there is no display driver in
// the gate), so this reads MainForm.cs's own four Columns.Add calls, in the order the ListView
// paints them, and pins both the order and that the widths sum inside leftPanel's own fixed
// width. The leftPanel width and its own literal 460 are read from the same source file, not
// hard-coded twice.
[TestClass]
public sealed class RowListLayoutTests
{
    private static string MainFormSource() =>
        File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Ui", "MainForm.cs"));

    private static readonly Regex ColumnAdd = new(@"_rowList\.Columns\.Add\(""([^""]+)"",\s*(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex LeftPanelWidth = new(@"leftPanel\s*=\s*new Panel\s*\{[^}]*Width\s*=\s*(\d+)", RegexOptions.Compiled);

    [TestMethod]
    public void StateIsTheSecondColumnRightAfterTheRowNumber()
    {
        MatchCollection matches = ColumnAdd.Matches(MainFormSource());
        Assert.AreEqual(4, matches.Count, "expected exactly four _rowList.Columns.Add calls");
        Assert.AreEqual("#", matches[0].Groups[1].Value);
        Assert.AreEqual("State", matches[1].Groups[1].Value, "State must be the second column, not pushed behind the two text columns");
    }

    [TestMethod]
    public void TheFourColumnWidthsFitInsideLeftPanelsOwnWidthWithRoomForAScrollbar()
    {
        string source = MainFormSource();
        MatchCollection columns = ColumnAdd.Matches(source);
        int total = columns.Sum(m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        Match panelMatch = LeftPanelWidth.Match(source);
        Assert.IsTrue(panelMatch.Success, "could not find leftPanel's own Width in MainForm.cs");
        int leftPanelWidth = int.Parse(panelMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // A vertical scrollbar and the control's own border take some of leftPanel's width; 30px
        // is a generous, deliberately conservative allowance for both on a default DPI.
        Assert.IsLessThanOrEqualTo(leftPanelWidth - 30, total,
            "column widths (" + total + ") do not comfortably fit inside leftPanel's width (" + leftPanelWidth + "); State would need a horizontal scroll to see");
    }
}
