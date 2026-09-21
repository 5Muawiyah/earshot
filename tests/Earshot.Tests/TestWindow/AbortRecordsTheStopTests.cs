using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// S8's own acceptance line: "Abort test writes result.json with the stop recorded and the at-rest
// offer shown." The shim's own throw on "A <seq>" reaching a script's catch/finally, and the
// Close-AtRest prompt's "Stop this computer grabbing your AirPods again?" heading, are both already proven elsewhere
// (ProtocolWireTests.AbortReachesTheScriptsFinally with a synthetic probe; PromptPresenterTests
// for the heading). This is the missing link: ChildRunner.Abort, called exactly as MainForm's own
// Stop button calls it, against a real shipped script and the real fake device, all the way to a
// result.json a real owner would read afterwards.
[TestClass]
public sealed class AbortRecordsTheStopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [TestMethod]
    public void AbortingTheFirstPromptStillLeavesAReadableResultRecordingTheStop()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string driver = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
        string script = Path.Combine(repoRoot, "tools", "live-tests", "01-A2dpOneShot.ps1");
        string runRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest", "20260920T000000Z");
        const string testId = "01-a2dp-oneshot";

        var options = new SandboxOptions { Folder = sandbox.Path };
        var extraArguments = new List<(string Name, string Value)>
        {
            ("SandboxRoot", sandbox.Path),
            ("TestId", testId),
            ("Case", "one"),
        };

        var messages = new System.Collections.Concurrent.BlockingCollection<ChildMessage>();
        var transcript = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var runner = new ChildRunner(
            host, driver, script, @"C:\nowhere\Earshot.exe", runRoot,
            resume: false, variant: 0, offerUninstall: false, allowPlanB: false,
            environmentOverrides: options.ChildEnvironment, extraArguments: extraArguments);
        runner.MessageReceived += message => messages.Add(message);
        runner.TranscriptLine += line => transcript.Enqueue(line);
        runner.Start();

        // The very first prompt (Show-Preconditions, "Ready?"): the same seq MainForm's own
        // OnStopClicked would send Abort for, since it is the only one pending right now.
        ChildMessage firstPrompt = WaitForPrompt(messages, transcript);
        runner.Abort(firstPrompt.Seq);

        // Whatever follows (a crash from the script's own catch, or a further prompt from its
        // closing at-rest check, exactly the "at-rest offer" S8 names) is answered the way a
        // satisfied owner would, until the child actually exits.
        bool sawExit = DriveToExit(runner, messages, transcript, out int exitCode);
        Assert.IsTrue(sawExit, "No exit arrived after the abort." + Environment.NewLine + string.Join(Environment.NewLine, transcript));
        Assert.IsTrue(runner.WaitForExit(Timeout));

        string resultPath = Path.Combine(runRoot, testId, "result.json");
        Assert.IsTrue(File.Exists(resultPath), "No result.json was written after the abort.");

        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, testId);
        Assert.IsNotNull(result, "result.json did not parse: " + failure);
        Assert.AreEqual("fail", result!.Overall);
        Assert.IsTrue(result.StoppedEarly, "the stop was not recorded as the 'run' criterion that means the script stopped early.");

        // StateDeriver's own already-pinned rule 5 ("a criterion with id 'run' means the script
        // stopped early: Failed, 'stopped early'"), reached this time through a real abort rather
        // than a fixture.
        var evidence = new List<RunEvidence> { EvidenceStore.ReadRunEvidence("20260920T000000Z", Path.Combine(runRoot, testId), testId) };
        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(testId), evidence, null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
        Assert.AreEqual("stopped early", state.Reason);
        Assert.IsFalse(state.IsGreen);
    }

    private static ChildMessage WaitForPrompt(
        System.Collections.Concurrent.BlockingCollection<ChildMessage> messages, System.Collections.Concurrent.ConcurrentQueue<string> transcript)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
            {
                break;
            }

            if (message.Kind == ChildMessageKind.Prompt)
            {
                return message;
            }
        }

        Assert.Fail("No prompt arrived first (only hello or nothing)." + Environment.NewLine + string.Join(Environment.NewLine, transcript));
        throw new InvalidOperationException("unreachable");
    }

    private static bool DriveToExit(
        ChildRunner runner, System.Collections.Concurrent.BlockingCollection<ChildMessage> messages,
        System.Collections.Concurrent.ConcurrentQueue<string> transcript, out int exitCode)
    {
        exitCode = -1;
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
            {
                return false;
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
                exitCode = message.ExitCode ?? -1;
                return true;
            }
            else if (message.Kind == ChildMessageKind.Crash)
            {
                // The abort escaping straight past the script's own catch (it has none broad
                // enough) still counts: Invoke-GuiHalf.ps1/the fakes driver reports it as a
                // crash, and result.json is whatever the script's own finally managed to write
                // before that, which the fail-closed reading of result.json judges on its own terms.
                exitCode = message.ExitCode ?? 3;
                return true;
            }
        }

        return false;
    }
}
