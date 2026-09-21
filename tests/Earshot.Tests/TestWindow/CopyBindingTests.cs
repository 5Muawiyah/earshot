using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The literal "Passed" appears in one source file, and a row
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

    // An unreadable HiberbootEnabled makes Get-FastStartupSetting return "unknown"
    // (LiveTest.psm1's own Get-FastStartupSetting), never "on" or "off". The sentence must never
    // claim a Fast Startup shut down was tested when the setting could not actually be read: that
    // would be inventing a fact this window has no source for.
    [TestMethod]
    public void UnknownFastStartupNeverClaimsAFastStartupShutDownWasTested()
    {
        string sentence = Copy.FastStartupSentence("unknown");
        Assert.IsFalse(sentence.Contains("Fast Startup shut down", StringComparison.Ordinal),
            "'unknown' must never be reported as though it confirmed a Fast Startup shut down: " + sentence);
        Assert.IsFalse(sentence.Contains("cold start", StringComparison.Ordinal),
            "'unknown' must never be reported as though it confirmed a cold start either: " + sentence);
    }

    [TestMethod]
    public void OffFastStartupSaysTheSettingIsSwitchedOff()
    {
        StringAssert.Contains(Copy.FastStartupSentence("off"), "switched off");
    }

    [TestMethod]
    public void OnFastStartupSaysTheSettingIsSwitchedOn()
    {
        StringAssert.Contains(Copy.FastStartupSentence("on"), "switched on");
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
