using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T7 / design.md section 7.4: a reply is written only from ChildRunner.ReplyFromOwnerClick, one
// reply per seq, and a second click for the same seq writes nothing.
[TestClass]
public sealed class ChildRunnerClickSafetyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    // design.md 7.4: "No timer, default, queue or startup path calls it." Scanned across the
    // whole project rather than just ChildRunner.cs, so a future caller anywhere (a form, a
    // timer tick) is caught too; today, before slice S4 wires a button to it, that count is
    // zero, which still satisfies "at most one".
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
