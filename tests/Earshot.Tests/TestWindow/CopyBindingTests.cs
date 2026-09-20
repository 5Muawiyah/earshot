using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T4 (test-gui.md section 13): the literal "Passed" appears in one source file, and a row
// presenter given Unknown, Not run or any amber state renders neither that word alone nor the
// green colour.
[TestClass]
public sealed class CopyBindingTests
{
    [TestMethod]
    public void TheWordPassedAppearsInExactlyOneSourceFile()
    {
        string sourceRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow");

        // The check is on the literal string "Passed" appearing in code, not in a comment that
        // merely talks about it (this file's own header does, for one), and not on every
        // reference to the Copy.Passed constant it defines.
        var withLiteral = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string codeOnly = StripComments(File.ReadAllText(file));
            if (System.Text.RegularExpressions.Regex.IsMatch(codeOnly, "\"Passed\""))
            {
                withLiteral.Add(Path.GetRelativePath(sourceRoot, file));
            }
        }

        CollectionAssert.AreEqual(new[] { Path.Combine("Ui", "Copy.cs") }, withLiteral,
            "The literal \"Passed\" must appear in Copy.cs only. Found in: " + string.Join(", ", withLiteral));
    }

    [TestMethod]
    public void UnknownNeverRendersAsPassedOrGreen()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Unknown, Reason = "no result.json" };
        AssertNeverGreenOrBarePassed(state);
    }

    [TestMethod]
    public void NotRunNeverRendersAsPassedOrGreen()
    {
        var state = new DerivedRowState { Kind = RowStateKind.NotRun };
        AssertNeverGreenOrBarePassed(state);
    }

    [TestMethod]
    public void AnAmberQualifiedPassNeverRendersAsBarePassedOrGreen()
    {
        var state = new DerivedRowState { Kind = RowStateKind.Passed, Qualifier = "on an earlier build" };
        AssertNeverGreenOrBarePassed(state);
        StringAssert.Contains(RowPresenter.Text(state), Copy.Passed, "The amber text should still say Passed, with its qualifier.");
    }

    [TestMethod]
    public void EveryNonPassedKindRendersWithoutTheWordPassedAtAll()
    {
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            if (kind == RowStateKind.Passed)
            {
                continue;
            }

            var state = new DerivedRowState { Kind = kind };
            string text = RowPresenter.Text(state);
            Assert.IsFalse(text.Contains(Copy.Passed, StringComparison.Ordinal), kind + " rendered as '" + text + "', which names Passed.");
        }
    }

    // Blanks // line comments and /* */ block comments, keeping line breaks so a match's line
    // number (not used here, but worth keeping honest) still lines up. Good enough for this
    // project's plain style; it does not need to survive a "//" inside a string literal, of
    // which this project's source has none in the files this test reads.
    private static string StripComments(string text)
    {
        string noBlock = System.Text.RegularExpressions.Regex.Replace(
            text, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')),
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    private static void AssertNeverGreenOrBarePassed(DerivedRowState state)
    {
        Assert.AreNotEqual(System.Drawing.Color.Green, RowPresenter.RowColor(state));
        Assert.AreNotEqual(Copy.Passed, RowPresenter.Text(state));
    }
}
