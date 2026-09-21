using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The first reply of a session
// arrives intact. Proved once against a build of ChildRunner whose StandardInputEncoding wrote a
// UTF-8 preamble instead of ASCII, and recorded red there (handover); this is the same test,
// green against the real ChildRunner, which sets StandardInputEncoding to ASCII for exactly this
// reason (see the comment in ChildRunner's constructor).
[TestClass]
public sealed class PreambleDefectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void TheFirstReplyOfASessionArrivesIntact()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var harness = new GuiHalfHarness(FirstPromptScript);
        harness.Start();

        ChildMessage prompt = harness.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);
        harness.Runner.ReplyFromOwnerClick(prompt.Seq, "y");

        harness.NextMessageOfKind(ChildMessageKind.Exit, Timeout);
        Assert.IsTrue(harness.Runner.WaitForExit(Timeout));
        Assert.AreEqual(0, harness.Runner.ExitCode);

        // With a UTF-8 preamble ahead of it, the first "y" arrives as three extra bytes plus "y"
        // (a local probe recorded the exact corruption: 180, 9559, 9488, 121), which this
        // exact-match assertion catches. ASCII, which ChildRunner actually uses, has no preamble,
        // so the line the script reads back is exactly what was sent.
        StringAssert.Contains(harness.Transcript, "FIRST-REPLY=[y]");
    }

    private const string FirstPromptScript = """
        param([Parameter(Mandatory = $true)][string]$ExePath, [Parameter(Mandatory = $true)][string]$RunRoot)
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        $reply = Read-Host 'first prompt of the session'
        Write-Host ('FIRST-REPLY=[' + $reply + ']')
        exit 0
        """;
}
