using System.Globalization;

namespace Earshot.TestWindow.Core;

// The only place a row's state is decided (test-gui.md section 6.2). Every branch below is named
// for the rule it implements; T2 (tests\Earshot.Tests\TestWindow\StateDeriverPinTests.cs) pins
// each one by deleting it and recording the red run.
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

        // section 8.3: "kill leaves Unknown and the banner, never a pass." A forced kill can
        // leave the real device mid-way through a live step, which no older evidence (even a
        // genuine earlier pass) can speak to, so this outranks everything else, but only for the
        // newest run: an older kill that a later, clean run has since superseded is just history.
        if (runsNewestFirst[0].HasKilledMarker)
        {
            return new DerivedRowState
            {
                Kind = RowStateKind.Unknown,
                Reason = "the test was stopped by force; nothing after that point is known",
            };
        }

        (RunEvidence? verdictRun, HalfKind verdictHalf, string? historyNote, RunEvidence? newestUnreadable) =
            FindVerdictRun(spec, runsNewestFirst);

        if (verdictRun is null)
        {
            // Fail closed: evidence exists but none of it proves anything either way. A read
            // failure outranks "stopped before any step", because unreadable evidence is not the
            // same claim as an honestly empty run.
            return newestUnreadable is not null
                ? new DerivedRowState { Kind = RowStateKind.Unknown, Reason = newestUnreadable.ReadFailureReason, HistoryNote = historyNote }
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

    // Section 6.2: "the newest stamp holding that TestId whose result.json records at least one
    // criterion, or for test 10's first halves the first-half finding". Runs that are well formed
    // but stopped before any step are skipped and remembered as history, newest one only. A read
    // failure is remembered separately: it never masquerades as "stopped before any step".
    private static (RunEvidence? Verdict, HalfKind Half, string? HistoryNote, RunEvidence? NewestUnreadable) FindVerdictRun(
        TestRowSpec spec, IReadOnlyList<RunEvidence> runsNewestFirst)
    {
        string? historyNote = null;
        RunEvidence? newestUnreadable = null;

        foreach (RunEvidence run in runsNewestFirst)
        {
            if (!run.ReadSucceeded)
            {
                newestUnreadable ??= run;
                continue;
            }

            ParsedResult result = run.Result!;
            HalfKind half = ClassifyHalf(spec, result);
            bool isFirstHalfFindingMarker = half == HalfKind.First && result.Criteria.Count == 0 &&
                spec.FirstHalfOnlyFindingNames.Count > 0 &&
                spec.FirstHalfOnlyFindingNames.Any(name => result.Findings.Any(f => f.Name == name));

            // section 9.2: starting a fresh run over a pending one sets the old one aside, only on
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
                return (run, half, historyNote, newestUnreadable);
            }

            historyNote ??= "a later start on " + FormatStampForHistory(run.Stamp) + " was stopped before any step";
        }

        return (null, HalfKind.NotApplicable, historyNote, newestUnreadable);
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

    // Section 6.2, "First half only": never the test's pass. resume.txt present is the ordinary
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

        // A first half with no resume.txt to continue it, and not 05's declined-restart shape:
        // section 6.2 does not say what this means, so it is read as evidence that does not
        // settle anything, fail closed, rather than guessed either way.
        return new DerivedRowState
        {
            Kind = RowStateKind.Unknown,
            Reason = "the first half was recorded, but there is no resume.txt to continue it",
            HistoryNote = historyNote,
            LeftAtRest = verdict.LeftAtRest,
        };
    }

    // Section 6.2, "Two halves": Passed needs the second half's pass, the first half's snapshot
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
        else if (run.FirstHalfSnapshot is null || run.FirstHalfSnapshot.Overall != "pass")
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

    // Base pass/fail/inconclusive plus the earlier-build check, which section 6.2 applies to
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

    private static string? EarlierBuildQualifier(ParsedResult result, string? chosenExePath, DateTimeOffset? chosenExeLastWriteUtc)
    {
        if (chosenExePath is null)
        {
            return null;
        }

        bool differentExe = result.Exe is not null &&
            !string.Equals(result.Exe.Trim(), chosenExePath.Trim(), StringComparison.OrdinalIgnoreCase);
        bool exeChangedSince = chosenExeLastWriteUtc is not null && result.StartedUtc is not null &&
            chosenExeLastWriteUtc.Value > result.StartedUtc.Value;

        return differentExe || exeChangedSince ? "on an earlier build" : null;
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
