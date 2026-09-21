using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A smoke test for Run-GuiHalfAgainstFakes.ps1 and DeviceStubs.ps1 against the real test 01
// script, before any UI exists: real Windows PowerShell 5.1, the real LiveTest.psm1 and
// 01-A2dpOneShot.ps1, the real five prompt helpers through the real shim, the fake device from
// the unchanged Fakes.psm1. This is what StepPanel/MainForm drive interactively in the sandbox
// walk-through; here it is driven the same way, through ChildRunner's own API.
[TestClass]
public sealed class SandboxFakesSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [TestMethod]
    public void Test01RunsToACleanPassAgainstTheFakeDevice()
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

        var options = new SandboxOptions { Folder = sandbox.Path };
        var extraArguments = new List<(string Name, string Value)>
        {
            ("SandboxRoot", sandbox.Path),
            ("TestId", "01-a2dp-oneshot"),
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

        // Answer every real prompt the way a satisfied owner would: yes throughout.
        int answered = 0;
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
                // Show-Preconditions and Confirm-Step's Yes sends "y",
                // Read-Answer's Yes sends the option word itself ("yes"), Wait-Owner's Yes sends
                // an empty line.
                string reply = message.Caller switch
                {
                    "Read-Answer" => "yes",
                    "Wait-Owner" => string.Empty,
                    _ => "y",
                };
                runner.ReplyFromOwnerClick(message.Seq, reply);
                answered++;
            }
            else if (message.Kind == ChildMessageKind.Exit)
            {
                Assert.AreEqual(0, message.ExitCode, "01-A2dpOneShot.ps1 did not exit 0 against the fake device.");
                sawExit = true;
                break;
            }
            else if (message.Kind == ChildMessageKind.Crash)
            {
                Assert.Fail("The sandboxed run crashed: " + message.Text + Environment.NewLine + "Transcript:" + Environment.NewLine + string.Join(Environment.NewLine, transcript));
            }
        }

        if (!sawExit)
        {
            Assert.Fail("No exit message arrived after " + answered + " answered prompt(s)." + Environment.NewLine +
                "Transcript:" + Environment.NewLine + string.Join(Environment.NewLine, transcript));
        }

        Assert.IsGreaterThan(0, answered, "No prompt was ever answered, so this proves nothing.");
        Assert.IsTrue(runner.WaitForExit(Timeout));
        Assert.AreEqual(0, runner.ExitCode);

        string resultPath = Path.Combine(runRoot, "01-a2dp-oneshot", "result.json");
        Assert.IsTrue(File.Exists(resultPath), "No result.json was written under the redirected LOCALAPPDATA at " + resultPath);

        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, "01-a2dp-oneshot");
        Assert.IsNotNull(result, "result.json did not parse: " + failure);
        Assert.AreEqual("pass", result!.Overall);
    }
}
