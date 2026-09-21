using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A reply is written only from ChildRunner.ReplyFromOwnerClick, one
// reply per seq, and a second click for the same seq writes nothing.
[TestClass]
public sealed class ChildRunnerClickSafetyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [TestMethod]
    public void ASecondClickForTheSameSeqIsIgnored()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var harness = new GuiHalfHarness(TwoPromptsScript);
        harness.Start();

        ChildMessage first = harness.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);
        harness.Runner.ReplyFromOwnerClick(first.Seq, "one");

        // A second, later click for the same seq: the script has already moved on to the second
        // prompt, so this stray line must never reach it as an answer to anything.
        harness.Runner.ReplyFromOwnerClick(first.Seq, "one-again");

        ChildMessage second = harness.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);
        Assert.AreNotEqual(first.Seq, second.Seq, "The second prompt reused the first seq, which should not happen.");
        harness.Runner.ReplyFromOwnerClick(second.Seq, "two");

        ChildMessage exit = harness.NextMessageOfKind(ChildMessageKind.Exit, Timeout);
        Assert.AreEqual(0, exit.ExitCode);
        StringAssert.Contains(harness.Transcript, "ANSWERS=one|two");
    }

    // A click racing an abort for the same seq must never still send a reply: once Stop has told
    // the script to give up on this prompt, a stray "one" arriving after must not be read as an
    // answer to it. Abort reserves the seq itself, before its own line is even written, so a
    // reply for the same seq called any time after (racing or not) is always the no-op a second
    // click already is.
    [TestMethod]
    public void AReplyForTheSameSeqAsAnAbortIsNeverSent()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var harness = new GuiHalfHarness(TwoPromptsScript);
        harness.Start();

        ChildMessage first = harness.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);
        harness.Runner.Abort(first.Seq);
        Assert.IsTrue(harness.Runner.HasReplyOrAbortBeenSentForTests(first.Seq),
            "Abort must reserve the seq immediately, not only after its own line is written.");

        // Races the abort: if this were still allowed through, the script would read "one" as the
        // first prompt's answer and carry on to ask a second one instead of stopping.
        harness.Runner.ReplyFromOwnerClick(first.Seq, "one");

        ChildMessage crash = harness.NextMessageOfKind(ChildMessageKind.Crash, Timeout);
        StringAssert.Contains(crash.Text, "Stopped from the test window at your request.");
    }

    // No timer, default, queue or startup path may call it. Scanned across the
    // whole project rather than just ChildRunner.cs, so a future caller anywhere (a form, a
    // timer tick) is caught too.
    [TestMethod]
    public void ReplyFromOwnerClickHasAtMostOneCallSiteOutsideItsOwnDeclaration()
    {
        string sourceRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow");
        var callSites = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("ReplyFromOwnerClick(", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isDeclaration = lines[i].Contains("void ReplyFromOwnerClick(", StringComparison.Ordinal);
                if (!isDeclaration)
                {
                    callSites.Add(Path.GetFileName(file) + ":" + (i + 1));
                }
            }
        }

        Assert.IsLessThanOrEqualTo(1, callSites.Count, "More than one call site for ReplyFromOwnerClick: " + string.Join(", ", callSites));
    }

    private const string TwoPromptsScript = """
        param([Parameter(Mandatory = $true)][string]$ExePath, [Parameter(Mandatory = $true)][string]$RunRoot)
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        $a = Read-Host 'first prompt'
        $b = Read-Host 'second prompt'
        Write-Host ('ANSWERS=' + $a + '|' + $b)
        exit 0
        """;
}
