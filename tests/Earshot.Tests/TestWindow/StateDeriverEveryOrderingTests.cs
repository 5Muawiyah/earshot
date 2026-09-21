using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// StateDeriverNewestUnreadableTests.cs already proved the newest run's own read failure stops the
// walk; what stayed broken is a read failure or a kill found anywhere else in the walk, not only
// at the very front. Before this fix, FindVerdictRun's loop skipped past an unreadable or killed
// run exactly the way it skips a declined start, so a declined start over a killed or unreadable
// run over a genuine older pass still read green: the loop walked straight through to the pass
// underneath. Every ordering of three runs drawn from {pass, declined, unreadable, killed} is
// checked here against an independently written predicate (walk from the newest; skip a declined
// start; the first killed or unreadable run stops the walk at Unknown; the first pass wins), so
// the two are unlikely to share the same mistake.
[TestClass]
public sealed class StateDeriverEveryOrderingTests
{
    private enum RunKind { Pass, Declined, Unreadable, Killed }

    private static readonly RunKind[] AllKinds = { RunKind.Pass, RunKind.Declined, RunKind.Unreadable, RunKind.Killed };

    [TestMethod]
    public void EveryOrderingOfThreeRunsMatchesTheIndependentWalkPredicate()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();

        foreach (RunKind newest in AllKinds)
        {
            foreach (RunKind middle in AllKinds)
            {
                foreach (RunKind oldest in AllKinds)
                {
                    var sequence = new[] { newest, middle, oldest };
                    IReadOnlyList<RunEvidence> runs = sequence
                        .Select((kind, index) => BuildEvidence(spec.TestId, kind, index))
                        .ToList();

                    ExpectedOutcome expected = IndependentlyExpectedOutcome(sequence);

                    DerivedRowState state = StateDeriver.Derive(spec, runs, chosenExePath: null, chosenExeLastWriteUtc: null);
                    ExpectedOutcome actual = state.Kind switch
                    {
                        RowStateKind.Passed => ExpectedOutcome.Passed,
                        RowStateKind.Unknown => ExpectedOutcome.Unknown,
                        RowStateKind.StoppedBeforeAnyStep => ExpectedOutcome.StoppedBeforeAnyStep,
                        _ => ExpectedOutcome.Other,
                    };

                    Assert.AreEqual(expected, actual,
                        "ordering " + string.Join(",", sequence) + " gave " + state.Kind + " (reason: " + state.Reason + ")");
                }
            }
        }
    }

    private enum ExpectedOutcome { Passed, Unknown, StoppedBeforeAnyStep, Other }

    // The rule in the deriver's own terms, written the other way round from how FindVerdictRun
    // walks: from the newest run, a declined start settles nothing and is skipped; the first
    // killed or unreadable run stops the walk at Unknown, whatever is underneath it; the first
    // pass, reached with nothing killed or unreadable above it, wins; running out of runs with
    // every one of them declined settles nothing at all.
    private static ExpectedOutcome IndependentlyExpectedOutcome(RunKind[] sequenceNewestFirst)
    {
        foreach (RunKind kind in sequenceNewestFirst)
        {
            switch (kind)
            {
                case RunKind.Declined:
                    continue;
                case RunKind.Killed:
                case RunKind.Unreadable:
                    return ExpectedOutcome.Unknown;
                case RunKind.Pass:
                    return ExpectedOutcome.Passed;
            }
        }

        return ExpectedOutcome.StoppedBeforeAnyStep;
    }

    private static RunEvidence BuildEvidence(string testId, RunKind kind, int index)
    {
        string stamp = "2026092" + index + "T000000Z";
        string folder = @"C:\nowhere\" + stamp;

        return kind switch
        {
            RunKind.Pass => new RunEvidence
            {
                Stamp = stamp,
                Folder = folder,
                Result = new ParsedResult
                {
                    Test = testId,
                    Overall = "pass",
                    Criteria = new[] { new CriterionRecord("only", "criterion text", "pass", string.Empty) },
                    Findings = Array.Empty<FindingRecord>(),
                    StepCount = 1,
                },
            },
            RunKind.Declined => new RunEvidence
            {
                Stamp = stamp,
                Folder = folder,
                Result = new ParsedResult
                {
                    Test = testId,
                    Overall = "inconclusive",
                    Criteria = Array.Empty<CriterionRecord>(),
                    Findings = Array.Empty<FindingRecord>(),
                    StepCount = 0,
                },
            },
            RunKind.Unreadable => new RunEvidence
            {
                Stamp = stamp,
                Folder = folder,
                Result = null,
                ReadFailureReason = "no result.json",
            },
            RunKind.Killed => new RunEvidence
            {
                Stamp = stamp,
                Folder = folder,
                Result = null,
                ReadFailureReason = "no result.json",
                HasKilledMarker = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
