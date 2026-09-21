using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// One execution of every real prompt helper the tests would
// otherwise fake, reproducing the local probe as a committed regression: real
// Windows PowerShell 5.1, the real LiveTest.psm1, a hand-built run context in a scratch folder,
// no device. All five helpers (Show-Preconditions, Confirm-Step, Read-Answer, Read-Note,
// Wait-Owner) plus one real Wait-Seconds wait run through the real ReadHostShim.ps1 and
// Invoke-GuiHalf.ps1, in a no-window child with redirected streams, and the window's own
// ChildRunner speaks the whole exchange.
[TestClass]
public sealed class ChildRunnerRealHelperTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void AllFiveRealPromptHelpersAndARealWaitRunThroughTheRealShim()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string modulePath = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "LiveTest.psm1");
        if (!File.Exists(modulePath))
        {
            Assert.Inconclusive("LiveTest.psm1 was not found at " + modulePath + ".");
        }

        using var harness = new GuiHalfHarness(ProbeScript.Replace("__MODULE_PATH__", modulePath.Replace("'", "''"), StringComparison.Ordinal));
        harness.Start();

        ChildMessage hello = harness.NextMessageOfKind(ChildMessageKind.Hello, StepTimeout);
        Assert.AreEqual(1, hello.ProtocolVersion);
        Assert.AreEqual("probe.ps1", hello.Script);
        Assert.AreEqual("first", hello.Half);

        // Show-Preconditions: bound carries Preconditions and PhysicalActions, never a member
        // named Prompt (that belongs to Confirm-Step, whose own parameter is named that).
        ChildMessage preconditions = harness.NextMessageOfKind(ChildMessageKind.Prompt, StepTimeout);
        Assert.AreEqual("Show-Preconditions", preconditions.Caller);
        Assert.IsTrue(preconditions.Bound.ContainsKey("Preconditions"));
        Assert.IsTrue(preconditions.Bound.ContainsKey("PhysicalActions"));
        harness.Runner.ReplyFromOwnerClick(preconditions.Seq, "y");

        // Confirm-Step: bound carries Prompt and Consequence.
        ChildMessage confirm = harness.NextMessageOfKind(ChildMessageKind.Prompt, StepTimeout);
        Assert.AreEqual("Confirm-Step", confirm.Caller);
        Assert.AreEqual("Probe live step", confirm.Bound["Prompt"]);
        Assert.AreEqual("Changes nothing; this is a probe.", confirm.Bound["Consequence"]);
        harness.Runner.ReplyFromOwnerClick(confirm.Seq, "y");

        // Read-Answer: bound carries Question and, since it was passed explicitly, Options.
        ChildMessage answer = harness.NextMessageOfKind(ChildMessageKind.Prompt, StepTimeout);
        Assert.AreEqual("Read-Answer", answer.Caller);
        Assert.AreEqual("Did the probe work?", answer.Bound["Question"]);
        harness.Runner.ReplyFromOwnerClick(answer.Seq, "yes");

        // Read-Note: bound carries Question only.
        ChildMessage note = harness.NextMessageOfKind(ChildMessageKind.Prompt, StepTimeout);
        Assert.AreEqual("Read-Note", note.Caller);
        Assert.AreEqual("Anything to note?", note.Bound["Question"]);
        harness.Runner.ReplyFromOwnerClick(note.Seq, "all clear");

        // Wait-Owner: bound carries Text. The reply is an empty line (a Yes with no typed text).
        ChildMessage waitOwner = harness.NextMessageOfKind(ChildMessageKind.Prompt, StepTimeout);
        Assert.AreEqual("Wait-Owner", waitOwner.Caller);
        Assert.AreEqual("Do the physical thing now.", waitOwner.Bound["Text"]);
        harness.Runner.ReplyFromOwnerClick(waitOwner.Seq, string.Empty);

        // Wait-Seconds runs for real, one second, in this no-window child with redirected
        // streams: Write-Progress must not throw and the child must still reach its own exit.
        ChildMessage exit = harness.NextMessageOfKind(ChildMessageKind.Exit, ExitTimeout);
        Assert.AreEqual(0, exit.ExitCode);
        Assert.IsTrue(harness.Runner.WaitForExit(ExitTimeout));
        Assert.AreEqual(0, harness.Runner.ExitCode);

        // summary.txt holds exactly what a console run writes: the real Write-Line/Write-Section
        // calls inside Show-Preconditions, Confirm-Step, Read-Answer, Read-Note and Wait-Owner,
        // not anything the shim or the driver wrote about itself.
        string summaryPath = Path.Combine(harness.RunRoot, "summary.txt");
        Assert.IsTrue(File.Exists(summaryPath), "The real helpers did not write summary.txt.");
        string summary = File.ReadAllText(summaryPath);
        StringAssert.Contains(summary, "Ready? y");
        StringAssert.Contains(summary, "LIVE STEP: Probe live step");
        StringAssert.Contains(summary, "What it does: Changes nothing; this is a probe.");
        StringAssert.Contains(summary, "QUESTION: Did the probe work?");
        StringAssert.Contains(summary, "Answer: yes");
        StringAssert.Contains(summary, "NOTE: Anything to note?");
        StringAssert.Contains(summary, "Answer: all clear");
        StringAssert.Contains(summary, "DO THIS: Do the physical thing now.");

        // Answers: the real Read-Answer and Read-Note both append to $Run.Answers, which the
        // probe script prints back over the transcript once it is done.
        StringAssert.Contains(harness.Transcript, "PROBE-ANSWERS-COUNT=2");
    }

    // A hand-built run context, the way tools\live-tests\selftest\Test-RealLauncher.ps1 builds
    // one for the real Invoke-Earshot: only the members the helpers actually read, no device, no
    // New-LiveTestRun (which would insist on a real Earshot.exe and write under
    // %LOCALAPPDATA%\Earshot\livetest).
    private const string ProbeScript = """
        param(
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        Microsoft.PowerShell.Core\Import-Module '__MODULE_PATH__' -Force

        New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
        $run = [ordered]@{
            ExePath        = $ExePath
            Folder         = $RunRoot
            SummaryPath    = (Join-Path $RunRoot 'summary.txt')
            AppLiveTest    = (Join-Path $RunRoot 'app-evidence-source')
            AppEvidence    = (Join-Path $RunRoot 'app-evidence')
            StepIndex      = 0
            Steps          = (New-Object System.Collections.ArrayList)
            Criteria       = (New-Object System.Collections.ArrayList)
            Findings       = (New-Object System.Collections.ArrayList)
            Answers        = (New-Object System.Collections.ArrayList)
            Errors         = (New-Object System.Collections.ArrayList)
            CopiedEvidence = (New-Object System.Collections.ArrayList)
            LastCopied     = @()
        }
        Set-Content -LiteralPath $run.SummaryPath -Value '' -Encoding UTF8

        $ready = Show-Preconditions -Run $run -Preconditions @('one thing must be true') -PhysicalActions @('do a physical thing first')
        if ($ready)
        {
            [void](Confirm-Step -Run $run -Prompt 'Probe live step' -Consequence 'Changes nothing; this is a probe.')
        }

        [void](Read-Answer -Run $run -Question 'Did the probe work?')
        [void](Read-Note -Run $run -Question 'Anything to note?')
        Wait-Owner -Run $run -Text 'Do the physical thing now.'
        Wait-Seconds -Run $run -Seconds 1 -Reason 'one real second, proving Write-Progress does not throw here'

        Write-Host ('PROBE-ANSWERS-COUNT=' + @($run.Answers).Count)
        exit 0
        """;
}
