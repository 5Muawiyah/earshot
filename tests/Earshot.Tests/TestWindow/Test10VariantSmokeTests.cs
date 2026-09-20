using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// test-gui.md section 6.1: "row 10 opens into five variant rows, one per -Variant, each with its
// own TestId and its own two halves." Real Windows PowerShell 5.1, the real 10-ShutdownMessages.ps1
// script, the real driver and shim, the fake device, both halves, exactly as MainForm's
// StartFreshRun/StartResumedSecondHalf drive any other two-half row through ChildRunner's own
// API. Proves the required S6 sandbox demonstration for at least one of the five variants.
//
// Variant 1 is the only one of the five this class can exercise: tools\live-tests\selftest\
// Fakes.psm1 (unchanged, off limits) defines StartStates only for
// "10-shutdown-messages-v1|first" and "|resume". Variants 2 to 5 have no fixture there at all,
// so Initialize-FakeMachine throws immediately for them; this is a gap in the self-test's own
// fixture data, not in the window (a real, non-sandboxed run never touches Fakes.psm1 or
// StartStates for any variant). Recorded here rather than worked around, since Fakes.psm1 cannot
// be edited.
[TestClass]
public sealed class Test10VariantSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly string[] KnownScripts = { "10-ShutdownMessages.ps1" };

    [TestMethod]
    public void Variant1RunsBothHalvesToARecordedResultAndAPendingRunIsFoundBetweenThem()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string driver = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
        string script = Path.Combine(repoRoot, "tools", "live-tests", "10-ShutdownMessages.ps1");
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string runRoot = Path.Combine(liveTestRoot, "20260920T000000Z");
        const string testId = "10-shutdown-messages-v1";

        var options = new SandboxOptions { Folder = sandbox.Path };

        // First half: no criteria at all, only the stateBeforeRestart finding (section 6.1's
        // half-marker table), so Get-LiveTestExitCode's own rule reads this as inconclusive (2),
        // never a pass. 2 is the correct, expected exit here.
        RunHalfToExit(host, driver, script, runRoot, options, testId, variant: 1, resume: false, out int exitCode);
        Assert.AreEqual(2, exitCode, "10-ShutdownMessages.ps1 variant 1's first half did not read inconclusive.");

        string testFolder = Path.Combine(runRoot, testId);
        string resumeTxtPath = Path.Combine(testFolder, "resume.txt");
        Assert.IsTrue(File.Exists(resumeTxtPath), "No resume.txt after the first half.");
        StringAssert.Contains(File.ReadAllText(resumeTxtPath), "-Variant 1");

        (ParsedResult? firstHalf, string? firstHalfFailure) = EvidenceStore.TryReadResult(Path.Combine(testFolder, "result.json"), testId);
        Assert.IsNotNull(firstHalf, "First half result.json did not parse: " + firstHalfFailure);
        Assert.AreEqual(0, firstHalf!.Criteria.Count, "The first half should record no criteria, only the stateBeforeRestart finding.");
        Assert.IsTrue(firstHalf.Findings.Any(f => f.Name == "stateBeforeRestart"));

        // Between the halves: the exact PendingRunFinder/ResumeFile path StartResumedSecondHalf
        // uses, against this same variant's own spec (TestRowSpecFixtures.Test10Variant1
        // mirrors DisplayRow.ToSpec() for this variant).
        TestRowSpec spec = TestRowSpecFixtures.Test10Variant1();
        PendingRun? pending = PendingRunFinder.Find(spec, liveTestRoot);
        Assert.IsNotNull(pending, "No pending run was found for variant 1 between the halves.");
        Assert.AreEqual(testFolder, pending!.Folder);

        bool parsed = ResumeFile.TryParse(
            resumeTxtPath, repoRoot, liveTestRoot, KnownScripts,
            out ResumeInstruction? instruction, out string? reason);
        Assert.IsTrue(parsed, reason);
        Assert.AreEqual(1, instruction!.Variant, "resume.txt's own -Variant did not read back as 1.");
        Assert.AreEqual(runRoot, instruction.RunRoot);
        Assert.IsTrue(instruction.Variant.HasValue);

        // Second half, resuming with exactly resume.txt's own values, landing on the same
        // variant (never a fresh root, never a different -Variant).
        RunHalfToExit(host, driver, script, instruction.RunRoot, options, testId, variant: instruction.Variant!.Value, resume: true, out int secondExitCode);

        (ParsedResult? secondHalf, string? secondHalfFailure) = EvidenceStore.TryReadResult(Path.Combine(testFolder, "result.json"), testId);
        Assert.IsNotNull(secondHalf, "Second half result.json did not parse: " + secondHalfFailure);
        Assert.IsTrue(secondHalf!.Criteria.Any(c => c.Id == "query-arrived"));
        Assert.IsTrue(secondHalf.Criteria.Any(c => c.Id == "end-arrived"));
        Assert.IsTrue(secondHalf.Criteria.Any(c => c.Id == "block-queued"));

        // The window never accepts the second half's own exit code alone as proof; a recorded
        // result (any overall) is what T5 asks for. The fake session-end mechanics are not this
        // test's concern.
        Assert.IsTrue(secondExitCode is 0 or 1 or 2, "unexpected exit code: " + secondExitCode);

        // No longer pending: the second half's own markers, not resume.txt's mere presence,
        // decide that (StateDeriver.ClassifyHalf).
        Assert.IsNull(PendingRunFinder.Find(spec, liveTestRoot), "Still reads as pending after the second half completed.");
    }

    private static string RunHalfToExit(
        string host, string driver, string script, string runRoot, SandboxOptions options, string testId, int variant, bool resume, out int exitCode)
    {
        var extra = new List<(string Name, string Value)>
        {
            ("SandboxRoot", options.Folder),
            ("TestId", testId),
            ("Case", "one"),
        };

        var messages = new System.Collections.Concurrent.BlockingCollection<ChildMessage>();
        var transcript = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var runner = new ChildRunner(
            host, driver, script, @"C:\nowhere\Earshot.exe", runRoot,
            resume: resume, variant: variant, offerUninstall: false, allowPlanB: false,
            environmentOverrides: options.ChildEnvironment, extraArguments: extra);
        runner.MessageReceived += message => messages.Add(message);
        runner.TranscriptLine += line => transcript.Enqueue(line);
        runner.Start();

        int localExitCode = -1;
        bool sawExit = false;
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
            {
                break;
            }

            if (message.Kind == ChildMessageKind.Prompt)
            {
                string reply = message.Caller switch
                {
                    "Read-Answer" => "yes",
                    "Wait-Owner" => string.Empty,
                    _ => "y",
                };
                runner.ReplyFromOwnerClick(message.Seq, reply);
            }
            else if (message.Kind == ChildMessageKind.Exit)
            {
                localExitCode = message.ExitCode ?? -1;
                sawExit = true;
                break;
            }
            else if (message.Kind == ChildMessageKind.Crash)
            {
                Assert.Fail("The sandboxed run crashed: " + message.Text + Environment.NewLine +
                    "Transcript:" + Environment.NewLine + string.Join(Environment.NewLine, transcript));
            }
        }

        Assert.IsTrue(sawExit, "No exit message arrived." + Environment.NewLine + "Transcript:" + Environment.NewLine + string.Join(Environment.NewLine, transcript));
        Assert.IsTrue(runner.WaitForExit(Timeout));
        exitCode = localExitCode;
        return runRoot;
    }
}
