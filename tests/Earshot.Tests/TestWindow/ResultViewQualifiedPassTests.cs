using System.Drawing;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The Result view's own single verdict line used to take only Kind, never the row's own
// Qualifier: "shut down not confirmed" or "on an earlier build" still showed a clean green "This
// test worked." with "All 1 check worked.", so 08's acceptance test could read clean without a
// confirmed power down. RowText/PlainRowText already refuse to shorten a qualified pass back to
// the bare word on the row itself; ResultPanel's own headline must say the same thing, in colour
// and in words, for every qualifier StateDeriver ever writes.
[TestClass]
public sealed class ResultViewQualifiedPassTests
{
    private static ParsedResult PassingResult() => new()
    {
        Test = "08-acceptance-power-cycle",
        Overall = "pass",
        Criteria = new[] { new CriterionRecord("c1", "criterion text", "pass", string.Empty) },
        Findings = Array.Empty<FindingRecord>(),
        StepCount = 1,
    };

    private static void AssertQualifiedPassReadsAmber(ResultPanel panel, string qualifier)
    {
        ResultPresentation presentation = ResultPresenter.Present(PassingResult(), @"C:\nowhere");
        panel.Show(presentation, showTechnicalDetails: false, RowStateKind.Passed, qualifier);

        Assert.AreEqual(
            Copy.ResultVerdictLine(RowStateKind.Passed, qualifier), panel.VerdictTextForTests,
            "qualifier=" + qualifier);
        StringAssert.Contains(panel.VerdictTextForTests, Copy.ResultVerdictQualifiedPassed,
            "a qualified pass must never read the clean \"" + Copy.ResultVerdictPassed + "\" sentence. qualifier=" + qualifier);
        Assert.AreNotEqual(Color.DarkGreen, panel.VerdictColorForTests,
            "a qualified pass must not show in the same colour as a clean pass. qualifier=" + qualifier);
        Assert.IsFalse(panel.VerdictTextForTests.StartsWith('✓'),
            "a qualified pass must never keep the plain pass tick. qualifier=" + qualifier);
        Assert.IsTrue(panel.VerdictTextForTests.StartsWith(Copy.QualifiedPassSymbol, StringComparison.Ordinal),
            "a qualified pass must show its own distinct symbol. qualifier=" + qualifier);

        // Show() alone, never switching MainView (the same shortcut
        // InconclusiveShowsTheExactCouldNotTellVerdictSentence/UnknownShowsTheExactHarshestVerdictSentence
        // already take), leaves the Result view's own container hidden, and WinForms folds a hidden
        // parent into every child's own Visible read; the label's own Text (Show sets it to
        // string.Empty exactly when it means to stay hidden) proves what Show itself decided,
        // without depending on that folding.
        Assert.IsFalse(string.IsNullOrWhiteSpace(panel.VerdictQualifierTextForTests), "qualifier=" + qualifier);
        Assert.AreEqual(Copy.ResultVerdictQualifierLine(qualifier), panel.VerdictQualifierTextForTests, "qualifier=" + qualifier);
    }

    [TestMethod]
    public void AShutDownNotConfirmedPassReadsAmberWithItsOwnPlainReason()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            ResultPanel panel = form.ResultPanelForTests;
            AssertQualifiedPassReadsAmber(panel, "shut down not confirmed");
            StringAssert.Contains(panel.VerdictQualifierTextForTests, "was not shut down completely");
        });
    }

    [TestMethod]
    public void AnEarlierBuildPassReadsAmberWithItsOwnPlainReason()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            ResultPanel panel = form.ResultPanelForTests;
            AssertQualifiedPassReadsAmber(panel, "on an earlier build");
            StringAssert.Contains(panel.VerdictQualifierTextForTests, "older copy of Earshot");
        });
    }

    // Every plain qualifier StateDeriver ever writes, plus a combined one (CombineQualifier joins
    // with "; "): a static field, never an inline array literal, so a repeated call site is never
    // flagged for reallocating the same constant list on every call (CA1861).
    private static readonly string[] EveryQualifier =
    {
        "on an earlier build",
        "second half only on record",
        "shut down not confirmed",
        "run order not confirmed",
        "restart half not run",
        "on an earlier build; second half only on record",
    };

    [TestMethod]
    public void EveryQualifierStateDeriverWritesReadsAmberOnTheResultView()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            ResultPanel panel = form.ResultPanelForTests;
            foreach (string qualifier in EveryQualifier)
            {
                AssertQualifiedPassReadsAmber(panel, qualifier);
            }

            // The combined case says both reasons, not only the first.
            panel.Show(ResultPresenter.Present(PassingResult(), @"C:\nowhere"), showTechnicalDetails: false,
                RowStateKind.Passed, "on an earlier build; second half only on record");
            StringAssert.Contains(panel.VerdictQualifierTextForTests, "older copy of Earshot");
            StringAssert.Contains(panel.VerdictQualifierTextForTests, "second half of this test is on record");
        });
    }

    // A clean pass (no qualifier) is untouched: still the plain "This test worked." in green, and
    // the qualifier line stays hidden.
    [TestMethod]
    public void ACleanPassWithNoQualifierIsUntouched()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            ResultPanel panel = form.ResultPanelForTests;
            ResultPresentation presentation = ResultPresenter.Present(PassingResult(), @"C:\nowhere");
            panel.Show(presentation, showTechnicalDetails: false, RowStateKind.Passed, verdictQualifier: null);

            Assert.AreEqual(Copy.ResultVerdictLine(RowStateKind.Passed), panel.VerdictTextForTests);
            StringAssert.Contains(panel.VerdictTextForTests, Copy.ResultVerdictPassed);
            Assert.AreEqual(Color.DarkGreen, panel.VerdictColorForTests);
            Assert.IsTrue(string.IsNullOrEmpty(panel.VerdictQualifierTextForTests));
        });
    }

    // The row's own plain state (Copy.PlainRowText) must say the same caveat the Result view now
    // does, not a different or milder one, for every qualifier above: neither ever shortens a
    // qualified pass back to the bare "Worked"/"This test worked.".
    [TestMethod]
    public void TheRowsPlainStateNeverShortensAQualifiedPassEitherForAnyQualifierAbove()
    {
        foreach (string qualifier in EveryQualifier)
        {
            var state = new DerivedRowState { Kind = RowStateKind.Passed, Qualifier = qualifier };
            string rowPlainText = Copy.PlainRowText(state);
            string resultVerdictLine = Copy.ResultVerdictLine(RowStateKind.Passed, qualifier);

            Assert.AreNotEqual(Copy.PlainPassed, rowPlainText, "qualifier=" + qualifier);
            Assert.IsFalse(resultVerdictLine.Contains(Copy.ResultVerdictPassed, StringComparison.Ordinal), "qualifier=" + qualifier);
        }
    }
}
