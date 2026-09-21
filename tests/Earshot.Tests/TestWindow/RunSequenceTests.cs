using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The counter beside run-all.json: monotonically increasing, never reused, survives a restart
// (a fresh read of the same file, exactly what reopening the window does), and written
// atomically (a reader never sees a half-written file).
[TestClass]
public sealed class RunSequenceTests
{
    [TestMethod]
    public void EachCallHandsOutAStrictlyLargerValue()
    {
        using var root = new TempFolder();
        long first = RunSequence.TakeNext(root.Path);
        long second = RunSequence.TakeNext(root.Path);
        long third = RunSequence.TakeNext(root.Path);

        Assert.IsTrue(second > first);
        Assert.IsTrue(third > second);
    }

    // "Survives restarts": a fresh call against the same liveTestRoot, as a reopened window makes,
    // must continue from where the counter file left off, never restart at 1.
    [TestMethod]
    public void ANewCallAgainstTheSameRootContinuesFromWhereTheFileLeftOff()
    {
        using var root = new TempFolder();
        RunSequence.TakeNext(root.Path);
        RunSequence.TakeNext(root.Path);
        long beforeRestart = RunSequence.TakeNext(root.Path);

        // Nothing here holds anything in memory between these two calls; the only thing carrying
        // state across them is the file itself, exactly as a real restart would leave it.
        long afterRestart = RunSequence.TakeNext(root.Path);

        Assert.AreEqual(beforeRestart + 1, afterRestart);
    }

    [TestMethod]
    public void ANeverBeforeSeenRootStartsAtOne()
    {
        using var root = new TempFolder();
        Assert.AreEqual(1, RunSequence.TakeNext(root.Path));
    }

    [TestMethod]
    public void TheCounterFileIsNeverLeftAsATemporaryFile()
    {
        using var root = new TempFolder();
        RunSequence.TakeNext(root.Path);

        string[] entries = Directory.GetFileSystemEntries(root.Path);
        Assert.AreEqual(1, entries.Length, "A .tmp file (or anything else) was left beside run-sequence.json.");
        Assert.AreEqual(RunSequence.CounterFileName, Path.GetFileName(entries[0]));
    }

    [TestMethod]
    public void EnsureMarkerWritesTheMarkerFileIntoTheRunFolder()
    {
        using var root = new TempFolder();
        string runFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");

        long value = RunSequence.EnsureMarker(root.Path, runFolder);

        Assert.AreEqual(value, RunSequence.TryReadMarker(runFolder));
    }

    // A resumed second half reuses the same run folder: it must never be issued a fresh number
    // just for resuming into it, or ordering between the two halves of the very same run becomes
    // meaningless.
    [TestMethod]
    public void EnsureMarkerNeverReissuesForAFolderThatAlreadyHasOne()
    {
        using var root = new TempFolder();
        string runFolder = Path.Combine(root.Path, "20260920T000000Z", "08-acceptance-power-cycle");

        long firstHalf = RunSequence.EnsureMarker(root.Path, runFolder);
        RunSequence.TakeNext(root.Path); // some other run folder takes the next number in between
        long secondHalf = RunSequence.EnsureMarker(root.Path, runFolder);

        Assert.AreEqual(firstHalf, secondHalf);
    }

    [TestMethod]
    public void TryReadMarkerIsNullForAFolderWithNoMarkerAtAll()
    {
        using var root = new TempFolder();
        string runFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(runFolder);

        Assert.IsNull(RunSequence.TryReadMarker(runFolder));
    }

    [TestMethod]
    public void TryReadMarkerIsNullForAnUnparsableMarker()
    {
        using var root = new TempFolder();
        string runFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(Path.Combine(runFolder, RunSequence.MarkerFileName), "not-a-number");

        Assert.IsNull(RunSequence.TryReadMarker(runFolder));
    }

    // The real caller: BeginRun assigns a sequence number the moment a half actually starts.
    [TestMethod]
    public void BeginRunAssignsARunFolderItsSequenceNumber()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        var row = new ManifestRow
        {
            Number = "SQ", Script = "unused.ps1", TestId = "run-sequence-test", Kind = "utility",
            Halves = 1, Name = "n", Title = "t", Proves = "p", Settles = "s",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            string driverPath = Path.Combine(sandbox.Path, "inert-driver.ps1");
            File.WriteAllText(driverPath, InertDriverScript);
            string resultFolder = Path.Combine(sandbox.Path, "run-sequence-result");

            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            form.BeginRunForTests(displayRow, runner, resultFolder);

            MainFormTestHarness.PumpUntil(() => RunSequence.TryReadMarker(resultFolder) is not null, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(RunSequence.TryReadMarker(resultFolder), "BeginRun did not assign a sequence number once Start() succeeded.");
        });
    }

    private const string InertDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        function Send-InertTestMessage($Object)
        {
            $json = ($Object | ConvertTo-Json -Compress -Depth 6)
            $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
            [System.Console]::Out.WriteLine('@@EARSHOT-TW@@ ' + $wire)
            [System.Console]::Out.Flush()
        }

        Send-InertTestMessage ([ordered]@{ type = 'hello'; protocol = 1; pid = $PID; psVersion = $PSVersionTable.PSVersion.ToString(); script = 'inert-test.ps1'; half = 'first' })
        Send-InertTestMessage ([ordered]@{ type = 'exit'; code = 0 })
        exit 0
        """;
}
