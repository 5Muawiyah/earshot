using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// One execution of the real launcher, on harmless programs. Nothing here starts a recording or elevates.
[TestClass]
public sealed class ProcessRunnerTests
{
    private static string System32(string name) => Path.Combine(Environment.SystemDirectory, name);

    [TestMethod]
    public void TheRealLauncherRunsWprHelpWithoutElevationAndReadsItsAnswer()
    {
        string wpr = System32("wpr.exe");
        if (!File.Exists(wpr))
        {
            Assert.Inconclusive("wpr.exe is not on this PC.");
        }

        ProcessRun run = new WindowsProcessRunner().Run(wpr, WprCommand.Help, TimeSpan.FromSeconds(30));

        Assert.IsTrue(run.Started, run.Problem);
        Assert.IsFalse(run.TimedOut);
        Assert.IsTrue(run.Output.Contains("wpr", StringComparison.OrdinalIgnoreCase), "Output was: " + run.Output);
    }

    // The two options the helper's command lines rely on, read from this Windows's own wpr.exe: the start help names both.
    // https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options
    [TestMethod]
    public void ThisWindowsWprKnowsTheRecordFolderAndInstanceNameOptions()
    {
        string wpr = System32("wpr.exe");
        if (!File.Exists(wpr))
        {
            Assert.Inconclusive("wpr.exe is not on this PC.");
        }

        ProcessRun run = new WindowsProcessRunner().Run(wpr, ["-help", "start"], TimeSpan.FromSeconds(30));

        Assert.IsTrue(run.Started, run.Problem);
        Assert.IsFalse(run.TimedOut);
        StringAssert.Contains(run.Output, "-recordtempto", "Output was: " + run.Output);
        StringAssert.Contains(run.Output, "-instancename", "Output was: " + run.Output);
    }

    [TestMethod]
    public void TheRealLauncherReturnsAProgramsExitCode()
    {
        ProcessRun run = new WindowsProcessRunner().Run(System32("cmd.exe"), ["/c", "exit 7"], TimeSpan.FromSeconds(30));

        Assert.IsTrue(run.Started);
        Assert.AreEqual(7, run.ExitCode);
    }

    [TestMethod]
    public void AProgramThatWaitsForKeysIsNotWaitedOnForever()
    {
        // pause reads the keyboard; the launcher gives it an empty input, so it ends at once.
        ProcessRun run = new WindowsProcessRunner().Run(System32("cmd.exe"), ["/c", "pause"], TimeSpan.FromSeconds(30));

        Assert.IsTrue(run.Started);
        Assert.IsFalse(run.TimedOut);
    }

    [TestMethod]
    public void ARunPastItsLimitIsKilledAndReportedAsTimedOut()
    {
        ProcessRun run = new WindowsProcessRunner().Run(System32("ping.exe"), ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500));

        Assert.IsTrue(run.Started);
        Assert.IsTrue(run.TimedOut);
    }

    [TestMethod]
    public void AProgramThatIsNotThereIsReportedNotStarted()
    {
        ProcessRun run = new WindowsProcessRunner().Run(System32("no-such-program.exe"), [], TimeSpan.FromSeconds(5));

        Assert.IsFalse(run.Started);
        Assert.IsNotNull(run.Problem);
    }
}
