namespace Earshot.TestWindow.Core;

internal sealed record PendingRun
{
    public required string TestId { get; init; }
    public required string Stamp { get; init; }
    public required string Folder { get; init; }
}

// A run is pending when its folder holds resume.txt, its result.json is a first-half result by
// the markers, and there is no gui-set-aside.txt. Read from disk only; the newest stamp holding
// such a folder wins, the same order EvidenceStore already sorts in.
internal static class PendingRunFinder
{
    internal static PendingRun? Find(TestRowSpec spec, string liveTestRoot)
    {
        ArgumentNullException.ThrowIfNull(spec);

        foreach ((string stamp, string folder) in EvidenceStore.FindRunFolders(liveTestRoot, spec.TestId))
        {
            if (!File.Exists(Path.Combine(folder, "resume.txt")) || File.Exists(Path.Combine(folder, "gui-set-aside.txt")))
            {
                continue;
            }

            // A killed second half leaves resume.txt from the first half still here, and
            // result.json still holding the first half's own stale data, since the killed script
            // never reached Complete-LiveTestRun to overwrite it. Without this, that stale record
            // read exactly like an untouched, never-attempted first-half pass, and the row offered
            // "Carry on with the second half" again, over evidence StateDeriver's own killed-marker
            // check already reads as Unknown.
            if (File.Exists(Path.Combine(folder, "gui-killed.txt")))
            {
                continue;
            }

            (ParsedResult? result, _) = EvidenceStore.TryReadResult(Path.Combine(folder, "result.json"), spec.TestId);
            if (result is null)
            {
                continue;
            }

            if (!IsFirstHalfResult(spec, result))
            {
                continue;
            }

            return new PendingRun { TestId = spec.TestId, Stamp = stamp, Folder = folder };
        }

        return null;
    }

    private static bool IsFirstHalfResult(TestRowSpec spec, ParsedResult result)
    {
        HalfKind half = StateDeriver.ClassifyHalf(spec, result);
        if (half == HalfKind.First)
        {
            return true;
        }

        // Test 10's first half: a finding and no criteria at all.
        return result.Criteria.Count == 0 && spec.FirstHalfOnlyFindingNames.Count > 0 &&
            spec.FirstHalfOnlyFindingNames.Any(name => result.Findings.Any(f => f.Name == name));
    }
}
