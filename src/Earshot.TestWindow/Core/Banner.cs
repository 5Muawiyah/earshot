namespace Earshot.TestWindow.Core;

internal enum BannerLevel
{
    None,
    Amber,
    Red,
}

// design.md section 4.6. Rows other than 00 Restore stay locked while Level is Red, until a
// newer Restore run records leftAtRest of yes or not-applicable.
internal sealed record BannerState
{
    public required BannerLevel Level { get; init; }
    public string? Message { get; init; }

    internal bool RowsLockedExceptRestore => Level == BannerLevel.Red;
}

// Computed from disk alone, at every open and after every half (design.md section 4.6): "if the
// newest result.json by finishedUtc has leftAtRest of no, unknown or no such finding, or if a
// run folder newer than it has no result.json" the banner is red; no-on-purpose is amber and
// blocks nothing; yes or not-applicable is no banner at all.
internal static class Banner
{
    internal const string RedMessage =
        "This PC may be left able to page the AirPods at the next start. Run Restore before you shut down.";

    internal static BannerState Compute(string liveTestRoot)
    {
        List<(string Stamp, bool HasResult, ParsedResult? Result)> runs = ScanAllRuns(liveTestRoot);
        if (runs.Count == 0)
        {
            // Nothing has ever run through this window: not evidence of anything either way.
            return new BannerState { Level = BannerLevel.None };
        }

        runs.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));

        (string Stamp, bool HasResult, ParsedResult? Result)? newestWithResult = null;
        foreach (var run in runs)
        {
            if (run.HasResult)
            {
                newestWithResult = run;
                break;
            }
        }

        if (newestWithResult is null)
        {
            // Every run folder that exists has no result.json at all: something started and
            // never finished, which is not a confirmed at-rest state either.
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
        }

        bool orphanNewer = runs.Any(run => !run.HasResult && string.CompareOrdinal(run.Stamp, newestWithResult.Value.Stamp) > 0);
        if (orphanNewer)
        {
            return new BannerState { Level = BannerLevel.Red, Message = RedMessage };
        }

        string? leftAtRest = newestWithResult.Value.Result!.LeftAtRest;
        return leftAtRest switch
        {
            "yes" or "not-applicable" => new BannerState { Level = BannerLevel.None },
            "no-on-purpose" => new BannerState { Level = BannerLevel.Amber, Message = "Left enabled on purpose." },
            _ => new BannerState { Level = BannerLevel.Red, Message = RedMessage },
        };
    }

    // Every run folder under the live test root, across every test, newest stamp first is not
    // guaranteed here (caller sorts): stamp, whether it has a readable result.json, and the
    // parsed result when it does. A folder whose result.json fails to parse counts as "no
    // result.json" (fail closed).
    private static List<(string Stamp, bool HasResult, ParsedResult? Result)> ScanAllRuns(string liveTestRoot)
    {
        var runs = new List<(string, bool, ParsedResult?)>();
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

            foreach (string testFolder in Directory.EnumerateDirectories(stampFolder))
            {
                string testId = Path.GetFileName(testFolder);
                (ParsedResult? result, _) = EvidenceStore.TryReadResult(Path.Combine(testFolder, "result.json"), testId);
                runs.Add((stamp, result is not null, result));
            }
        }

        return runs;
    }
}
