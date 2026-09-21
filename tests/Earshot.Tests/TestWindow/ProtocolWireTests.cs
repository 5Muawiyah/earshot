using System.Diagnostics;
using System.Text;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The shim's own contract, proved against a real child rather than
// only read. These tests write raw bytes to a child's stdin directly, which is the shim's own
// grammar being tested, not the "one reply per click" rule ChildRunner enforces (that is
// ChildRunnerClickSafetyTests). The skill's rail 1 ("never pipe input into a script") is
// unaffected: everything here goes through the same protocol a real click would, just written by
// hand instead of a button.
[TestClass]
public sealed class ProtocolWireTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void AWrongSeqReplyMakesTheShimThrowAndTheScriptRecordsIt()
    {
        using RawChild child = StartProbe(OnePromptScript);
        ChildMessage prompt = child.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);

        // The correct seq is prompt.Seq; this answers a different one instead.
        int wrongSeq = prompt.Seq + 1;
        child.WriteRawLine(Protocol.FormatReply(wrongSeq, "y"));

        ChildMessage crash = child.NextMessageOfKind(ChildMessageKind.Crash, Timeout);
        StringAssert.Contains(crash.Text, "could not read");
        Assert.IsTrue(child.WaitForExit(Timeout));
        Assert.AreEqual(3, child.ExitCode);
    }

    [TestMethod]
    public void AMalformedReplyMakesTheShimThrowAndTheScriptRecordsIt()
    {
        using RawChild child = StartProbe(OnePromptScript);
        child.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);

        child.WriteRawLine("this is not a reply at all");

        ChildMessage crash = child.NextMessageOfKind(ChildMessageKind.Crash, Timeout);
        StringAssert.Contains(crash.Text, "could not read");
        Assert.IsTrue(child.WaitForExit(Timeout));
        Assert.AreEqual(3, child.ExitCode);
    }

    [TestMethod]
    public void EndOfInputMakesTheShimThrowThatTheWindowClosed()
    {
        using RawChild child = StartProbe(OnePromptScript);
        child.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);

        child.CloseInput();

        ChildMessage crash = child.NextMessageOfKind(ChildMessageKind.Crash, Timeout);
        StringAssert.Contains(crash.Text, "closed before this was answered");
        Assert.IsTrue(child.WaitForExit(Timeout));
        Assert.AreEqual(3, child.ExitCode);
    }

    // Abort reaches the script's own catch and finally: the shim throws "Stopped from the test
    // window at your request.", which is not caught inside the probe script (mirroring a real
    // script's own top-level try/finally round its at-rest close), so it escapes to
    // Invoke-GuiHalf.ps1's own catch as a crash, after the probe's finally block has already run
    // and left its own marker on disk.
    [TestMethod]
    public void AbortReachesTheScriptsFinally()
    {
        using RawChild child = StartProbe(PromptWithFinallyScript);
        ChildMessage prompt = child.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);

        child.WriteRawLine(Protocol.FormatAbort(prompt.Seq));

        ChildMessage crash = child.NextMessageOfKind(ChildMessageKind.Crash, Timeout);
        StringAssert.Contains(crash.Text, "Stopped from the test window at your request");
        Assert.IsTrue(child.WaitForExit(Timeout));
        Assert.AreEqual(3, child.ExitCode);
        Assert.IsTrue(File.Exists(child.FinallyMarkerPath), "The probe script's finally block did not run.");
    }

    // A child that dies before it can send its own exit message (here, the target script fails
    // to parse, which throws outside Invoke-GuiHalf.ps1's own try/catch): a child that exits
    // without an exit message is reported as such. No exit or crash
    // message ever arrives; the row this feeds is derived from disk alone, never from the
    // process's own exit code.
    [TestMethod]
    public void AChildThatDiesBeforeItsOwnExitMessageIsDetectedByItsAbsence()
    {
        using RawChild child = StartProbe("this ( is not valid PowerShell {{{");

        ChildMessage hello = child.NextMessageOfKind(ChildMessageKind.Hello, Timeout);
        Assert.AreEqual("first", hello.Half);

        Assert.IsTrue(child.WaitForExit(Timeout), "The child did not exit at all.");
        Assert.AreNotEqual(0, child.ExitCode);
        Assert.IsFalse(child.SawExitMessage, "An exit message arrived even though the script never parsed.");
        Assert.IsFalse(child.SawCrashMessage, "A crash message arrived, but this failure happens before the driver's own try/catch.");
    }

    // caller is not one of the five known helpers: a script that calls Read-Host directly, the
    // way an unmaintained corner of a script might. The protocol reports exactly what happened;
    // deciding that this is kind Unknown and offering only "Stop the test" is StepPanel's job
    // (slice S4), not this layer's.
    [TestMethod]
    public void ARawReadHostCallReportsACallerThatIsNoneOfTheFiveHelpers()
    {
        using RawChild child = StartProbe(RawReadHostScript);
        ChildMessage prompt = child.NextMessageOfKind(ChildMessageKind.Prompt, Timeout);

        string[] known = { "Show-Preconditions", "Confirm-Step", "Read-Answer", "Read-Note", "Wait-Owner" };
        CollectionAssert.DoesNotContain(known, prompt.Caller);
        Assert.AreEqual("A raw prompt nobody wraps", prompt.Prompt);

        child.WriteRawLine(Protocol.FormatReply(prompt.Seq, "whatever"));
        child.NextMessageOfKind(ChildMessageKind.Exit, Timeout);
    }

    private static RawChild StartProbe(string scriptBody)
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        return new RawChild(host, scriptBody);
    }

    private const string OnePromptScript = """
        param([Parameter(Mandatory = $true)][string]$ExePath, [Parameter(Mandatory = $true)][string]$RunRoot)
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        [void](Read-Host 'one prompt')
        exit 0
        """;

    private const string RawReadHostScript = """
        param([Parameter(Mandatory = $true)][string]$ExePath, [Parameter(Mandatory = $true)][string]$RunRoot)
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        [void](Read-Host 'A raw prompt nobody wraps')
        exit 0
        """;

    private const string PromptWithFinallyScript = """
        param([Parameter(Mandatory = $true)][string]$ExePath, [Parameter(Mandatory = $true)][string]$RunRoot)
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
        try
        {
            [void](Read-Host 'a prompt that will be aborted')
        }
        finally
        {
            Set-Content -LiteralPath (Join-Path $RunRoot 'finally-ran.txt') -Value 'finally ran' -Encoding UTF8
        }
        exit 0
        """;

    // A raw child of Invoke-GuiHalf.ps1, for tests that need to write bytes the shim's own
    // grammar rejects, which ChildRunner's API (ReplyFromOwnerClick, Abort) never lets a caller
    // build. It reuses the same PowerShell51.CreateStartInfo the product uses, so the encoding
    // and the removed PSModulePath are identical to a real run.
    private sealed class RawChild : IDisposable
    {
        private readonly Process _process;
        private readonly TempFolder _scriptFolder = new();
        private readonly System.Collections.Concurrent.BlockingCollection<ChildMessage> _messages = new();

        internal RawChild(string host, string scriptBody)
        {
            string scriptPath = _scriptFolder.File("probe.ps1");
            File.WriteAllText(scriptPath, scriptBody);
            RunRoot = _scriptFolder.File("run");
            Directory.CreateDirectory(RunRoot);

            string driver = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
            var arguments = new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", driver,
                "-Script", scriptPath, "-ExePath", @"C:\nowhere\Earshot.exe", "-RunRoot", RunRoot,
            };

            ProcessStartInfo info = PowerShell51.CreateStartInfo(host, arguments);
            info.StandardInputEncoding = Encoding.ASCII;
            info.StandardOutputEncoding = Encoding.UTF8;

            _process = new Process { StartInfo = info };
            _process.Start();
            _ = Task.Run(ReadLoop);
        }

        internal string RunRoot { get; }

        internal string FinallyMarkerPath => Path.Combine(RunRoot, "finally-ran.txt");

        internal bool SawExitMessage { get; private set; }

        internal bool SawCrashMessage { get; private set; }

        private void ReadLoop()
        {
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                if (!Protocol.IsProtocolLine(line))
                {
                    continue;
                }

                ChildMessage message = Protocol.ParseMessage(line);
                if (message.Kind == ChildMessageKind.Exit)
                {
                    SawExitMessage = true;
                }

                if (message.Kind == ChildMessageKind.Crash)
                {
                    SawCrashMessage = true;
                }

                _messages.Add(message);
            }
        }

        internal ChildMessage NextMessageOfKind(ChildMessageKind kind, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!_messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
                {
                    break;
                }

                if (message.Kind == kind)
                {
                    return message;
                }
            }

            throw new TimeoutException("No message of kind " + kind + " arrived within " + timeout + ".");
        }

        internal void WriteRawLine(string line)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }

        internal void CloseInput() => _process.StandardInput.Close();

        internal bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

        internal int ExitCode => _process.ExitCode;

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the check and the kill.
                }
            }

            _process.Dispose();
            _scriptFolder.Dispose();
        }
    }
}
