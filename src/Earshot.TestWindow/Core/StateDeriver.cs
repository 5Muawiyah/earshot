using System.Globalization;

namespace Earshot.TestWindow.Core;

// The only place a row's state is decided. Every branch below is named for the rule it
// implements; tests\Earshot.Tests\TestWindow\StateDeriverPinTests.cs pins each one by deleting it
// and recording the red run.
internal static class StateDeriver
{
    internal static DerivedRowState Derive(
        TestRowSpec spec,
        IReadOnlyList<RunEvidence> runsNewestFirst,
        string? chosenExePath,
        DateTimeOffset? chosenExeLastWriteUtc)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(runsNewestFirst);

        if (runsNewestFirst.Count == 0)
        {
            return new DerivedRowState { Kind = RowStateKind.NotRun };
        }

        (RunEvidence? verdictRun, HalfKind verdictHalf, string? historyNote, StopReason? stopReason) =
            FindVerdictRun(spec, runsNewestFirst);

        if (verdictRun is null)
        {
            // Fail closed: evidence exists but none of it proves anything either way. Walking from
            // the newest run, a run that settled nothing (a declined start, stopped before any
            // step) is skipped, but the first run that is killed or unreadable stops the walk right
            // there: neither is the same claim as an honestly empty run, and no older evidence,
            // not even a genuine earlier pass underneath it, is ever consulted while either stands.
            return stopReason is not null
                ? new DerivedRowState { Kind = RowStateKind.Unknown, Reason = stopReason.Reason, HistoryNote = stopReason.HistoryNote ?? historyNote }
                : new DerivedRowState { Kind = RowStateKind.StoppedBeforeAnyStep, HistoryNote = historyNote };
        }

        ParsedResult verdict = verdictRun.Result!;

        if (spec.Halves == 1)
        {
            return BuildLeafState(verdictRun, verdict, chosenExePath, chosenExeLastWriteUtc, historyNote, qualifierSuffix: null);
        }

        if (verdictHalf == HalfKind.NotApplicable)
        {
            return new DerivedRowState
            {
                Kind = RowStateKind.Unknown,
                Reason = "result.json's criteria match neither half's markers for this test",
                HistoryNote = historyNote,
            };
        }

        if (verdictHalf == HalfKind.First)
        {
            return DeriveFromFirstHalf(spec, verdictRun, verdict, chosenExePath, chosenExeLastWriteUtc, historyNote);
        }

        return DeriveFromSecondHalf(spec, verdictRun, verdict, chosenExePath, chosenExeLastWriteUtc, historyNote);
    }

    // Why the walk below stopped without ever reaching a verdict: a kill (this window's own
    // forced hard stop, which can leave the real device mid-way through a live step) or a read
    // failure (missing, truncated, empty, for the wrong test, or otherwise failing EvidenceStore's
    // own fail-closed checks). Either one outranks every older run, including a genuine earlier
    // pass, because neither is the same claim as an honestly empty "stopped before any step" run.
    private sealed record StopReason(string? Reason, string? HistoryNote);

    // The verdict run is the newest stamp holding that TestId whose result.json records at least one
    // criterion, or for test 10's first halves the first-half finding. Walking from the newest run,
    // a run that settled nothing (stopped before any step, or a set-aside first half) is skipped
    // and remembered as history, newest one only; the first run that is killed or unreadable stops
    // the walk immediately, at whatever position it is found, rather than being skipped past on the
    // way to older evidence.
    private static (RunEvidence? Verdict, HalfKind Half, string? HistoryNote, StopReason? StopReason) FindVerdictRun(
        TestRowSpec spec, IReadOnlyList<RunEvidence> runsNewestFirst)
    {
        string? historyNote = null;

        foreach (RunEvidence run in runsNewestFirst)
        {
            if (run.HasKilledMarker)
            {
                return (null, HalfKind.NotApplicable, historyNote,
                    new StopReason("the test was stopped by force; nothing after that point is known", historyNote));
            }

            if (!run.ReadSucceeded)
            {
                return (null, HalfKind.NotApplicable, historyNote,
                    new StopReason(run.ReadFailureReason, "a run in " + run.Folder + " could not be read; no older result is trusted while that stands"));
            }

            ParsedResult result = run.Result!;
            HalfKind half = ClassifyHalf(spec, result);
            bool isFirstHalfFindingMarker = half == HalfKind.First && result.Criteria.Count == 0 &&
                spec.FirstHalfOnlyFindingNames.Count > 0 &&
                spec.FirstHalfOnlyFindingNames.Any(name => result.Findings.Any(f => f.Name == name));

            // Starting a fresh run over a pending one sets the old one aside, only on
            // a Yes. A set-aside first half is no longer this row's pending evidence; it is
            // skipped here exactly as a stopped-before-any-step run is, so an older real verdict
            // underneath it is still found.
            if (half == HalfKind.First && run.HasSetAsideFile)
            {
                historyNote ??= "a later start on " + FormatStampForHistory(run.Stamp) + " was set aside";
                continue;
            }

            bool countsAsVerdict = result.Criteria.Count > 0 || isFirstHalfFindingMarker;
            if (countsAsVerdict)
            {
                return (run, half, historyNote, null);
            }

            historyNote ??= "a later start on " + FormatStampForHistory(run.Stamp) + " was stopped before any step";
        }

        return (null, HalfKind.NotApplicable, historyNote, null);
    }

    internal static HalfKind ClassifyHalf(TestRowSpec spec, ParsedResult result)
    {
        if (spec.Halves == 1)
        {
            return HalfKind.NotApplicable;
        }

        bool hasSecondMarker = result.Criteria.Any(c => spec.SecondHalfOnlyCriteriaIds.Contains(c.Id, StringComparer.Ordinal));
        if (hasSecondMarker)
        {
            return HalfKind.Second;
        }

        bool hasFirstCriterionMarker = result.Criteria.Any(c => spec.FirstHalfOnlyCriteriaIds.Contains(c.Id, StringComparer.Ordinal));
        bool hasFirstFindingMarker = result.Criteria.Count == 0 && spec.FirstHalfOnlyFindingNames.Count > 0 &&
            spec.FirstHalfOnlyFindingNames.Any(name => result.Findings.Any(f => f.Name == name));
        if (hasFirstCriterionMarker || hasFirstFindingMarker)
        {
            return HalfKind.First;
        }

        return HalfKind.NotApplicable;
    }

    // A first half is never the test's pass on its own. resume.txt present is the ordinary
    // case (Waiting); its exception is 05, where no resume.txt means the owner declined the
    // optional restart and the first half is the whole test.
    private static DerivedRowState DeriveFromFirstHalf(
        TestRowSpec spec, RunEvidence run, ParsedResult verdict, string? chosenExePath, DateTimeOffset? chosenExeLastWriteUtc, string? historyNote)
    {
        if (spec.FirstHalfAloneIsCompleteWhenDeclined && !run.HasResumeFile && !run.HasSetAsideFile)
        {
            return BuildLeafState(run, verdict, chosenExePath, chosenExeLastWriteUtc, historyNote, qualifierSuffix: "restart half not run");
        }

        if (run.HasResumeFile)
        {
            RowStateKind waitingKind = spec.PowerCycleRequirement == PowerCycleRequirement.FullShutDown
                ? RowStateKind.WaitingForShutDown
                : RowStateKind.WaitingForRestart;
            return new DerivedRowState { Kind = waitingKind, HistoryNote = historyNote, LeftAtRest = verdict.LeftAtRest };
        }

        // A first half with no resume.txt to continue it, and not 05's declined-restart shape,
        // is a shape the fail-closed rules do not cover, so it is read as evidence that does not
        // settle anything, fail closed, rather than guessed either way.
        return new DerivedRowState
        {
            Kind = RowStateKind.Unknown,
            Reason = "the first half was recorded, but there is no resume.txt to continue it",
            HistoryNote = historyNote,
            LeftAtRest = verdict.LeftAtRest,
        };
    }

    // For a two-half test, Passed needs the second half's pass, the first half's snapshot
    // must itself have passed, and 08/09 need a confirmed power-down.
    private static DerivedRowState DeriveFromSecondHalf(
        TestRowSpec spec, RunEvidence run, ParsedResult verdict, string? chosenExePath, DateTimeOffset? chosenExeLastWriteUtc, string? historyNote)
    {
        DerivedRowState leaf = BuildLeafState(run, verdict, chosenExePath, chosenExeLastWriteUtc, historyNote, qualifierSuffix: null);
        if (leaf.Kind != RowStateKind.Passed)
        {
            return leaf;
        }

        if (!run.HasFirstHalfSnapshotFile)
        {
            leaf = leaf with { Qualifier = CombineQualifier(leaf.Qualifier, "second half only on record") };
        }
        else if (run.FirstHalfSnapshot is null || !IsAcceptableFirstHalfSnapshot(spec, run.FirstHalfSnapshot))
        {
            return new DerivedRowState
            {
                Kind = RowStateKind.Failed,
                Reason = "the first half's snapshot did not pass",
                HistoryNote = historyNote,
                LeftAtRest = verdict.LeftAtRest,
            };
        }

        if (spec.PowerCycleRequirement == PowerCycleRequirement.FullShutDown &&
            !string.Equals(run.PowerCycleVerdict, "power-down", StringComparison.Ordinal))
        {
            leaf = leaf with { Qualifier = CombineQualifier(leaf.Qualifier, "shut down not confirmed") };
        }

        return leaf;
    }

    // Whether a first-half snapshot counts as good enough for the second half's pass to stand.
    // Derived from the manifest shape, not a hard-coded test number: a test whose first half never
    // records a criterion at all (only a finding; today only test 10) always recomputes to
    // "inconclusive" by design, never "pass", so "inconclusive" is accepted there too. Any other
    // test's first half is still held to an actual "pass"; a first half that genuinely failed
    // (recomputes to "fail", e.g. a stopped-early 'run' criterion) is never accepted, for any test.
    private static bool IsAcceptableFirstHalfSnapshot(TestRowSpec spec, ParsedResult snapshot)
    {
        if (snapshot.Overall == "pass")
        {
            return true;
        }

        bool firstHalfIsInconclusiveByDesign = spec.FirstHalfOnlyCriteriaIds.Count == 0 && spec.FirstHalfOnlyFindingNames.Count > 0;
        return firstHalfIsInconclusiveByDesign && snapshot.Overall == "inconclusive";
    }

    // Base pass/fail/inconclusive plus the earlier-build check, which the fail-closed rules apply to
    // every row, one half or two.
    private static DerivedRowState BuildLeafState(
        RunEvidence run, ParsedResult result, string? chosenExePath, DateTimeOffset? chosenExeLastWriteUtc,
        string? historyNote, string? qualifierSuffix)
    {
        RowStateKind kind = result.Overall switch
        {
            "pass" => RowStateKind.Passed,
            "fail" => RowStateKind.Failed,
            _ => RowStateKind.Inconclusive,
        };

        // A 'run' criterion (the script stopped early) always recomputes to a failing overall
        // already; this is fail-closed insurance, not the ordinary path.
        if (result.StoppedEarly)
        {
            kind = RowStateKind.Failed;
        }

        string? reason = result.StoppedEarly ? "stopped early" : null;
        string? qualifier = null;

        if (kind == RowStateKind.Passed)
        {
            qualifier = EarlierBuildQualifier(result, chosenExePath, chosenExeLastWriteUtc);
            if (qualifierSuffix is not null)
            {
                qualifier = CombineQualifier(qualifier, qualifierSuffix);
            }
        }
        else if (qualifierSuffix is not null)
        {
            qualifier = qualifierSuffix;
        }

        return new DerivedRowState
        {
            Kind = kind,
            Qualifier = qualifier,
            Reason = reason,
            HistoryNote = historyNote,
            LeftAtRest = result.LeftAtRest,
        };
    }

    // The earlier-build check must fail closed. An absent or unparsable exe or startedUtc in
    // result.json, or a chosen exe that cannot currently be found (chosenExeLastWriteUtc null
    // while chosenExePath is set, which is what MainForm passes when File.Exists(_exePath) is
    // false), each used to compare as "not different" and "not changed since", so the row read a
    // clean green pass with no build actually confirmed. Unknown build is a qualifier now, never
    // silently green.
    private static string? EarlierBuildQualifier(ParsedResult result, string? chosenExePath, DateTimeOffset? chosenExeLastWriteUtc)
    {
        if (chosenExePath is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.Exe))
        {
            return "build not confirmed: result.json does not record which exe ran it";
        }

        if (!string.Equals(result.Exe.Trim(), chosenExePath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "on an earlier build";
        }

        if (result.StartedUtc is null)
        {
            return "build not confirmed: result.json does not record when the run started";
        }

        if (chosenExeLastWriteUtc is null)
        {
            return "build not confirmed: the chosen exe could not be found to check it";
        }

        return chosenExeLastWriteUtc.Value > result.StartedUtc.Value ? "on an earlier build" : null;
    }

    private static string? CombineQualifier(string? existing, string addition) =>
        existing is null ? addition : existing + "; " + addition;

    private static string FormatStampForHistory(string stamp)
    {
        if (DateTime.TryParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
        {
            return parsed.ToString("d MMMM", CultureInfo.InvariantCulture);
        }

        return stamp;
    }
}
