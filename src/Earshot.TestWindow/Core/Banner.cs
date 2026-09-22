namespace Earshot.TestWindow.Core;

internal enum BannerLevel
{
    None,
    Amber,
    Red,
}

// Rows other than 00 Restore stay locked while Level is Red, until the newest readable result
// (from any test that ends at rest, not only a Restore run) records leftAtRest of yes or
// not-applicable. Red never says which exact cause it is (no readable run at all, a newer
// unreadable or killed folder, a sequence/order disagreement, or leftAtRest itself reading "no",
// "unknown" or missing): LiveTest.psm1's own at-rest check writes leftAtRest "no" whenever the
// nodes read anything but Blocked, which includes a declined offer with the AirPods still on the
// phone, so a "no" is never trusted to mean the AirPods are known connected to this computer
// either. Every Red cause gets the same advice (Copy.AtRestNo): never a click, only put the
// AirPods in their case and run Restore. IsDuplicateRecordCause is the one exception to "never
// says which cause": whether two folders sharing the exact same sequence number is among the
// reasons is a fact about the evidence on disk, not a guess about the device, so it is named
// (Copy.AtRestDuplicateRecord) rather than folded into the same wording as everything else.
internal sealed record BannerState
{
    public required BannerLevel Level { get; init; }
    public string? Message { get; init; }
    public bool IsDuplicateRecordCause { get; init; }

    internal bool RowsLockedExceptRestore => Level == BannerLevel.Red;
}

// Computed from disk alone, at every open and after every half: if the newest result.json by
// finishedUtc has leftAtRest of no, unknown or no such finding, or if a run folder newer than it
// has no result.json, the banner is red; no-on-purpose is amber and blocks nothing; yes or
// not-applicable is no banner at all.
internal static class Banner
{
    internal const string RedMessage =
        "This PC may be left able to page the AirPods at the next start. Run Restore before you shut down.";

    // "Newest" is decided by finishedUtc, the moment the result was actually written, never by
    // the run folder's own stamp. A resumed second half writes into
    // its first half's (older) stamp folder, so ordering by stamp let a stale no/unknown there
    // hide behind an unrelated test's newer-stamped but chronologically earlier pass. A run
    // whose result.json cannot be read at all (missing, truncated, empty, wrong test, or
    // self-disagreeing) is ordered by its folder's own stamp instead, since that is the only time
    // anything is known about it, and it can never become the chosen result, only a reason to
    // distrust one that is newer than it thought.
    // activeFolder (MainForm's own _activeResultFolder, or null when nothing is running) is the
    // one run folder a stale-looking gui-run-started.txt is never read as abandoned for: it is
    // this window's own currently active half, not one that died together with the window.
    internal static BannerState Compute(string liveTestRoot, string? activeFolder = null)
    {
        List<ScannedRun> runs = ScanAllRuns(liveTestRoot, activeFolder);
        if (runs.Count == 0)
        {
            // Nothing has ever run through this window: not evidence of anything either way.
            return new BannerState { Level = BannerLevel.None };
        }

        ScannedRun? chosen = null;
        foreach (ScannedRun run in runs)
        {
            if (!run.ReadSucceeded)
            {
                continue;
            }

            if (chosen is null || IsNewerThan(run, chosen))
            {
                chosen = run;
            }
        }

        if (chosen is null)
        {
            // Nothing anywhere has a readable result: fail closed, the same as the old "every run
            // folder has no result.json" rule.
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
        }

        bool newerUnreadableExists = runs.Any(run => !run.ReadSucceeded && IsNewerThan(run, chosen));
        if (newerUnreadableExists)
        {
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
        }

        // One signal or the other was wrong about which run actually happened later (the one
        // situation neither a real event time nor a sequence alone can be fully trusted in):
        // at-rest, being safety-critical, is never read from a chosen run while that stands,
        // whatever it says. Compared against each run's own OrderingUtc (the newest moment
        // anything is actually known to have happened in it), never against the folder's own bare
        // stamp: a resumed half reuses its first half's folder, keeping that folder's original
        // stamp forever however much later its own newest event actually happened, so comparing
        // against the stamp read every legitimate resume-after-an-intervening-run as a
        // disagreement, permanently, since the stamp itself could never catch up.
        EvidenceStore.SequenceOrderCheck orderCheck = EvidenceStore.DetectSequenceOrderIssue(
            runs.Select(run => (run.Sequence, run.OrderingUtc)).ToList());
        if (orderCheck.Disagrees)
        {
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage, IsDuplicateRecordCause = orderCheck.IsDuplicateRecord };
        }

        string? leftAtRest = chosen.Result!.LeftAtRest;
        return leftAtRest switch
        {
            "yes" or "not-applicable" => new BannerState { Level = BannerLevel.None },
            "no-on-purpose" => new BannerState { Level = BannerLevel.Amber, Message = "Left enabled on purpose." },
            _ => new BannerState { Level = BannerLevel.Red, Message = RedMessage },
        };
    }

    // Sequence decides it when both runs have a marker (RunSequence, immune to the system clock);
    // a run without one falls back to OrderingUtc, exactly as every run did before this existed.
    private static bool IsNewerThan(ScannedRun a, ScannedRun b) =>
        a.Sequence is long sa && b.Sequence is long sb ? sa > sb : a.OrderingUtc > b.OrderingUtc;

    private sealed record ScannedRun(string Stamp, ParsedResult? Result, DateTimeOffset OrderingUtc, bool ReadSucceeded, long? Sequence);

    // Every run folder under the live test root, across every test: its own result (when its
    // result.json is readable at all), and the timestamp it is ordered by, its own finishedUtc
    // when the result carries one, else the folder's own stamp (the real harness always writes
    // finishedUtc; only a fixture or a hand-edited file would not).
    private static List<ScannedRun> ScanAllRuns(string liveTestRoot, string? activeFolder)
    {
        var runs = new List<ScannedRun>();
        if (!Directory.Exists(liveTestRoot))
        {
            return runs;
        }

        foreach (string stampFolder in Directory.EnumerateDirectories(liveTestRoot))
        {
            string stamp = Path.GetFileName(stampFolder);
            if (!EvidenceStore.IsRunStamp(stamp))
            {
                continue;
            }

            DateTimeOffset stampUtc = EvidenceStore.ParseStampUtc(stamp);

            foreach (string testFolder in Directory.EnumerateDirectories(stampFolder))
            {
                string testId = Path.GetFileName(testFolder);

                // The administrator prompt rehearsal touches no device node and proves nothing
                // about rest: its own result always carries a hard-coded leftAtRest of
                // not-applicable, which must never be able to clear or outrank a real test's
                // not-at-rest or unknown state, so it is not even a candidate here.
                if (string.Equals(testId, ElevationGate.RehearsalTestId, StringComparison.Ordinal))
                {
                    continue;
                }

                (ParsedResult? result, _) = EvidenceStore.TryReadResult(Path.Combine(testFolder, "result.json"), testId);

                // A killed run (the window's own forced hard stop) leaves whatever result.json it found
                // on disk untouched, most often a second half's own first-half snapshot never
                // reached Complete-LiveTestRun to overwrite. That stale record can carry any
                // leftAtRest a first half legitimately records; treated as an ordinary readable
                // result here, it used to let a kill hide behind its own victim's old data and
                // show no banner at all. A killed folder is never trusted, the same as an
                // unreadable one: it can outrank a good result that is older than it, but it can
                // never become the chosen one itself.
                //
                // Ordering for a killed or unreadable folder is the newest time anything in it was
                // written (for a kill, that is the kill marker itself), never the stale result's
                // own finishedUtc: a kill that happens long after an older pass elsewhere used to
                // still read as "older" than that pass, by the stale first half's own clock, and so
                // never outranked it or showed the banner at all.
                // A stale gui-run-started.txt (the half started but this window never saw it end,
                // most often because the window died together with its own child before
                // MarkUnknownAndReset ever wrote gui-killed.txt) is read exactly like a kill, for
                // the same reason: whatever result.json is still sitting underneath it must never
                // speak for it. Exempted only for this window's own currently active run.
                bool staleRunStarted = File.Exists(Path.Combine(testFolder, "gui-run-started.txt")) &&
                    !(activeFolder is not null && string.Equals(testFolder, activeFolder, StringComparison.OrdinalIgnoreCase));

                // A sequence number above anything RunSequence has ever actually issued was never
                // handed out by a real half: a hand-forged gui-sequence.txt, read exactly like a
                // kill, since whatever result.json sits underneath it (however clean it looks,
                // however far in the future its own finishedUtc claims to be) is not evidence of
                // what actually happened. The forged number itself still stands for ordering (it is
                // never dropped from ScannedRun.Sequence below), so it can still outrank a genuine
                // result older than it and force the banner to show, but it can never itself become
                // the chosen, trusted result.
                long? sequence = RunSequence.TryReadMarker(testFolder);
                bool sequenceForged = sequence is long seq && seq > RunSequence.PeekLastIssued(liveTestRoot);

                bool killed = File.Exists(Path.Combine(testFolder, "gui-killed.txt")) || staleRunStarted || sequenceForged;
                bool readable = result is not null && !killed;
                DateTimeOffset ordering = EvidenceStore.ComputeEventTimeUtc(testFolder, result, readable, stampUtc);
                runs.Add(new ScannedRun(stamp, result, ordering, readable, sequence));
            }
        }

        return runs;
    }
}
