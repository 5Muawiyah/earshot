using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The result panel's plain-mode summary: a short sentence first (how many worked, did not work,
// could not tell), then one plain line per check that did not pass, from wording.json's own
// "check" entries or a neutral fallback when none exists yet. The technical table underneath
// (ResultPanel's own Failures/FailureRow machinery) is unchanged: this is presentation only.
[TestClass]
public sealed class ResultPresenterPlainSummaryTests
{
    private static ParsedResult BuildResult(string overall, params (string Id, string Outcome)[] criteria)
    {
        var criteriaRecords = criteria.Select(c => new CriterionRecord(c.Id, c.Id + " criterion text", c.Outcome, "detail")).ToList();
        return new ParsedResult
        {
            Test = "01", Overall = overall, Criteria = criteriaRecords, Findings = Array.Empty<FindingRecord>(),
            Errors = Array.Empty<string>(), StepCount = criteriaRecords.Count,
        };
    }

    [TestMethod]
    public void AllPassingChecksSummariseAsAllWorked()
    {
        ParsedResult result = BuildResult("pass", ("a", "pass"), ("b", "pass"), ("c", "pass"), ("d", "pass"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");
        Assert.AreEqual("All 4 checks worked.", Copy.PlainCheckSummarySentence(presentation.TotalCriteria, presentation.FailedCount, presentation.InconclusiveCount));
    }

    [TestMethod]
    public void SomeFailingChecksSummariseWithTheCountAndTotal()
    {
        ParsedResult result = BuildResult("fail", ("a", "pass"), ("b", "fail"), ("c", "fail"), ("d", "pass"), ("e", "pass"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");
        Assert.AreEqual("2 of 5 checks did not work.", Copy.PlainCheckSummarySentence(presentation.TotalCriteria, presentation.FailedCount, presentation.InconclusiveCount));
    }

    [TestMethod]
    public void SomeInconclusiveChecksSummariseAsCouldNotTell()
    {
        ParsedResult result = BuildResult("inconclusive", ("a", "pass"), ("b", "inconclusive"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");
        Assert.AreEqual("We could not tell for 1 check.", Copy.PlainCheckSummarySentence(presentation.TotalCriteria, presentation.FailedCount, presentation.InconclusiveCount));
    }

    [TestMethod]
    public void FailedAndInconclusiveBothAppearInOneSentence()
    {
        ParsedResult result = BuildResult("fail", ("a", "fail"), ("b", "inconclusive"), ("c", "pass"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");
        string sentence = Copy.PlainCheckSummarySentence(presentation.TotalCriteria, presentation.FailedCount, presentation.InconclusiveCount);
        StringAssert.Contains(sentence, "1 of 3 checks did not work.");
        StringAssert.Contains(sentence, "We could not tell for 1 check.");
    }

    [TestMethod]
    public void AFailedCheckWithAWordingEntryUsesItsOwnPlainNameAndMeaning()
    {
        var wording = new List<WordingEntry>
        {
            new("01", WordingKind.Check, "topology-reachable",
                "This computer can reach the AirPods before doing anything to them.", Array.Empty<WordingChoice>()) { HowTo = Array.Empty<HowToBlock>() },
        };
        ParsedResult result = BuildResult("fail", ("topology-reachable", "fail"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere", "01", wording);

        Assert.AreEqual(1, presentation.PlainFailureLines.Count);
        PlainCheckLine line = presentation.PlainFailureLines[0];
        Assert.IsTrue(line.HasWordingEntry);
        Assert.AreEqual("This computer can reach the AirPods before doing anything to them.", line.PlainLine);
    }

    [TestMethod]
    public void AFailedCheckWithNoWordingEntryFallsBackToANeutralLine()
    {
        ParsedResult result = BuildResult("fail", ("some-brand-new-check", "fail"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere", "01", new List<WordingEntry>());

        Assert.AreEqual(1, presentation.PlainFailureLines.Count);
        PlainCheckLine line = presentation.PlainFailureLines[0];
        Assert.IsFalse(line.HasWordingEntry);
        Assert.AreEqual("One check did not work. Show technical details to see which.", line.PlainLine);
    }

    [TestMethod]
    public void AnInconclusiveCheckWithNoWordingEntryUsesItsOwnNeutralLine()
    {
        ParsedResult result = BuildResult("inconclusive", ("some-brand-new-check", "inconclusive"));
        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere", "01", new List<WordingEntry>());

        PlainCheckLine line = presentation.PlainFailureLines[0];
        Assert.AreEqual("We could not tell for one check. Show technical details to see which.", line.PlainLine);
    }
}
