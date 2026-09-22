using System.Globalization;

namespace Earshot.TestWindow.Core;

internal enum BannerLevel
{
    None,
    Amber,
    Red,
}

// Rows other than 00 Restore stay locked while Level is Red, until the newest readable result
// (from any test that ends at rest, not only a Restore run) records leftAtRest of yes or
// not-applicable.
internal sealed record BannerState
{
    public required BannerLevel Level { get; init; }
    public string? Message { get; init; }

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
        // situation neither a stamp nor a sequence alone can be fully trusted in): at-rest, being
        // safety-critical, is never read from a chosen run while that stands, whatever it says.
        bool disagreement = EvidenceStore.SequenceDisagreesWithStampOrder(
            runs.Select(run => (run.Sequence, run.Stamp)).ToList());
        if (disagreement)
        {
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
        }

        // A second, separate cross-check against each run's own OrderingUtc (already the newest
        // of a kill marker's write time, a readable result's finishedUtc, or the folder's stamp):
        // a run folder's stamp never moves once a resumed second half writes back into it, so two
        // folders can agree with each other about sequence-vs-stamp order and still both be wrong,
        // when a run folder's sequence was never brought up to date with what actually last
        // happened inside it (a kill, or a second half's own finish) after another run's folder
        // was created in between. Whenever the sequence and the real newest-write order disagree
        // about which of two runs happened later, that is itself never resolved silently.
        if (SequenceDisagreesWithOrderingUtc(runs))
        {
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
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

    // True when any two scanned runs carry different sequence numbers whose order contradicts the
    // order of their own OrderingUtc (each already the newest thing known to have happened inside
    // that run folder). Two different run folders sharing the same sequence number is never
    // compared here: EvidenceStore.SequenceDisagreesWithStampOrder already fails closed on that,
    // via the stamp-based check just above this one in Compute.
    private static bool SequenceDisagreesWithOrderingUtc(List<ScannedRun> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            for (int j = i + 1; j < runs.Count; j++)
            {
                if (runs[i].Sequence is not long si || runs[j].Sequence is not long sj || si == sj)
                {
                    continue;
                }

                bool iNewerBySequence = si > sj;
                bool iNewerByOrdering = runs[i].OrderingUtc > runs[j].OrderingUtc;
                if (iNewerBySequence != iNewerByOrdering)
                {
                    return true;
                }
            }
        }

        return false;
    }

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

            DateTimeOffset stampUtc = ParseStampUtc(stamp);

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
                bool killed = File.Exists(Path.Combine(testFolder, "gui-killed.txt")) || staleRunStarted;
                bool readable = result is not null && !killed;
                DateTimeOffset ordering = readable ? result!.FinishedUtc ?? stampUtc : NewestWriteTimeUtc(testFolder, stampUtc);
                long? sequence = RunSequence.TryReadMarker(testFolder);
                runs.Add(new ScannedRun(stamp, result, ordering, readable, sequence));
            }
        }

        return runs;
    }

    // The stamp format EvidenceStore.IsRunStamp already validated (^\d{8}T\d{6}Z$), so this
    // always parses for anything that reached here.
    private static DateTimeOffset ParseStampUtc(string stamp) =>
        DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // The newest last-write time of any file directly in this folder (result.json, resume.txt,
    // gui-killed.txt and the rest), falling back to the run's own stamp when the folder holds
    // nothing at all: a kill or a read failure is ordered by whatever actually happened last, not
    // by a stale record's own idea of when it finished.
    private static DateTimeOffset NewestWriteTimeUtc(string testFolder, DateTimeOffset fallback)
    {
        DateTimeOffset newest = fallback;
        if (!Directory.Exists(testFolder))
        {
            return newest;
        }

        foreach (string file in Directory.EnumerateFiles(testFolder))
        {
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            if (written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }
}
