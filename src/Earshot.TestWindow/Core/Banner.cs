using System.Globalization;

namespace Earshot.TestWindow.Core;

internal enum BannerLevel
{
    None,
    Amber,
    Red,
}

// Rows other than 00 Restore stay locked while Level is Red, until a newer Restore run records
// leftAtRest of yes or not-applicable.
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
    internal static BannerState Compute(string liveTestRoot)
    {
        List<ScannedRun> runs = ScanAllRuns(liveTestRoot);
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

            if (chosen is null || run.OrderingUtc > chosen.OrderingUtc)
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

        bool newerUnreadableExists = runs.Any(run => !run.ReadSucceeded && run.OrderingUtc > chosen.OrderingUtc);
        if (newerUnreadableExists)
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

    private sealed record ScannedRun(string Stamp, ParsedResult? Result, DateTimeOffset OrderingUtc, bool ReadSucceeded);

    // Every run folder under the live test root, across every test: its own result (when its
    // result.json is readable at all), and the timestamp it is ordered by, its own finishedUtc
    // when the result carries one, else the folder's own stamp (the real harness always writes
    // finishedUtc; only a fixture or a hand-edited file would not).
    private static List<ScannedRun> ScanAllRuns(string liveTestRoot)
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
                (ParsedResult? result, _) = EvidenceStore.TryReadResult(Path.Combine(testFolder, "result.json"), testId);

                // A killed run (the window's own forced hard stop) leaves whatever result.json it found
                // on disk untouched, most often a second half's own first-half snapshot never
                // reached Complete-LiveTestRun to overwrite. That stale record can carry any
                // leftAtRest a first half legitimately records; treated as an ordinary readable
                // result here, it used to let a kill hide behind its own victim's old data and
                // show no banner at all. A killed folder is never trusted, the same as an
                // unreadable one: it can outrank a good result that is older than it, but it can
                // never become the chosen one itself.
                bool killed = File.Exists(Path.Combine(testFolder, "gui-killed.txt"));
                DateTimeOffset ordering = result?.FinishedUtc ?? stampUtc;
                runs.Add(new ScannedRun(stamp, result, ordering, result is not null && !killed));
            }
        }

        return runs;
    }

    // The stamp format EvidenceStore.IsRunStamp already validated (^\d{8}T\d{6}Z$), so this
    // always parses for anything that reached here.
    private static DateTimeOffset ParseStampUtc(string stamp) =>
        DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
