namespace Earshot.TestWindow.Core;

// test-gui.md section 10.2's lock rule: "15, 00's uninstall variant and 07's plan B are Locked
// until the newest rehearsal result.json is a pass and is newer than the last write time of
// LiveTest.psm1, Invoke-GuiHalf.ps1 and ReadHostShim.ps1." Reads disk only, decides nothing about
// starting anything: MainForm still never elevates and never runs the rehearsal itself.
internal static class ElevationGate
{
    internal const string RehearsalTestId = "elevated-launch-rehearsal";

    internal static bool IsUnlocked(ParsedResult? rehearsal, DateTimeOffset harnessNewestWriteUtc)
    {
        if (rehearsal is null || rehearsal.Overall != "pass" || rehearsal.FinishedUtc is null)
        {
            return false;
        }

        return rehearsal.FinishedUtc.Value > harnessNewestWriteUtc;
    }

    // The three files whose write time the lock is measured against: a change to any of them
    // (the shim, the driver, or the module's own prompt helpers) means the last rehearsal no
    // longer speaks for the code currently running.
    internal static DateTimeOffset HarnessNewestWriteUtc(string repoRoot)
    {
        string[] files =
        {
            Path.Combine(repoRoot, "tools", "live-tests", "LiveTest.psm1"),
            Path.Combine(repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1"),
            Path.Combine(repoRoot, "tools", "live-tests", "gui", "ReadHostShim.ps1"),
        };

        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (string file in files)
        {
            if (File.Exists(file))
            {
                DateTimeOffset writeTime = File.GetLastWriteTimeUtc(file);
                if (writeTime > newest)
                {
                    newest = writeTime;
                }
            }
        }

        return newest;
    }

    // The newest elevated-launch-rehearsal\result.json across every stamp under the live test
    // root, read the same fail-closed way as any other test's evidence (EvidenceStore.TryReadResult):
    // a malformed or self-disagreeing rehearsal file never counts as a pass.
    internal static ParsedResult? FindNewestRehearsal(string liveTestRoot)
    {
        ParsedResult? newest = null;
        DateTimeOffset newestFinished = DateTimeOffset.MinValue;

        foreach ((string _, string folder) in EvidenceStore.FindRunFolders(liveTestRoot, RehearsalTestId))
        {
            (ParsedResult? result, _) = EvidenceStore.TryReadResult(Path.Combine(folder, "result.json"), RehearsalTestId);
            if (result?.FinishedUtc is DateTimeOffset finished && finished > newestFinished)
            {
                newest = result;
                newestFinished = finished;
            }
        }

        return newest;
    }
}
