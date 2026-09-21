using System.Diagnostics;
using System.Text;
using Earshot.TestWindow.Core;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// tools\live-tests\selftest\Fakes.psm1's own stub text ends with a backstop: "function
// Start-Process { throw (...) }", so a helper that grew a new way of starting a process stops the
// self-test rather than quietly running it. DeviceStubs.ps1 (the sandbox twin ChildRunner drives
// through the window's own real protocol) never had that backstop: only Resolve-EarshotExe,
// Invoke-Earshot and Invoke-EarshotElevated were replaced, and the real Start-Process cmdlet was
// left reachable. This proves the gap is closed, and that a sandboxed run is safe even when it is
// handed an -ExePath that cannot exist (which the window will always be doing here, since
// Resolve-EarshotExe's own stub never reads it): calling the real cmdlet with an impossible path
// only ever fails fast on "the system cannot find the path", never over the network and never by
// starting anything real, which is what makes it safe to prove the backstop by actually calling it.
[TestClass]
public sealed class SandboxStartProcessBackstopTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void StartProcessThrowsTheNamedBackstopInsideTheSandboxTwinRatherThanReachingTheRealCmdlet()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new Earshot.Tests.TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string deviceStubs = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "DeviceStubs.ps1");
        string liveTestModule = Path.Combine(repoRoot, "tools", "live-tests", "LiveTest.psm1");
        Assert.IsTrue(File.Exists(deviceStubs), "DeviceStubs.ps1 was not found at " + deviceStubs);

        // An impossible path on purpose: a drive letter this machine does not have, so even the
        // real, unstubbed Start-Process cmdlet fails immediately and locally if the backstop below
        // is somehow bypassed, rather than starting anything or hanging on a network name.
        string probeScript = Path.Combine(sandbox.Path, "probe.ps1");
        File.WriteAllText(probeScript, string.Join(Environment.NewLine, new[]
        {
            "Set-StrictMode -Version 2.0",
            "$ErrorActionPreference = 'Stop'",
            ". '" + deviceStubs + "'",
            "Initialize-FakeMachine -SandboxRoot '" + sandbox.Path + "' -TestId '01-a2dp-oneshot' -Half 'first' -Case 'one'",
            "Microsoft.PowerShell.Core\\Import-Module '" + liveTestModule + "' -Force",
            "Install-EarshotTwDeviceStubs",
            "try { Start-Process -FilePath 'Q:\\this-drive-does-not-exist\\Earshot.exe' -ErrorAction Stop }",
            "catch { Write-Output ('CAUGHT: ' + $_.Exception.Message) }",
        }));

        string output = RunScript(host, probeScript);
        StringAssert.Contains(output, "CAUGHT:", "Start-Process did not throw at all: " + output);
        StringAssert.Contains(output, "never starts a process", "Start-Process reached the real cmdlet instead of the backstop: " + output);
    }

    [TestMethod]
    public void DrivingAHalfWithAnExePathThatCannotExistStillCompletes()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new Earshot.Tests.TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string driver = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
        string script = Path.Combine(repoRoot, "tools", "live-tests", "01-A2dpOneShot.ps1");
        string runRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest", "20260921T000000Z");
        var sandboxOptions = new SandboxOptions { Folder = sandbox.Path };

        var extraArguments = new List<(string Name, string Value)>
        {
            ("SandboxRoot", sandbox.Path),
            ("TestId", "01-a2dp-oneshot"),
            ("Case", "one"),
        };

        var messages = new System.Collections.Concurrent.BlockingCollection<ChildMessage>();
        using var runner = new ChildRunner(
            host, driver, script, @"Q:\this-drive-does-not-exist\Earshot.exe", runRoot,
            resume: false, variant: 0, offerUninstall: false, allowPlanB: false,
            environmentOverrides: sandboxOptions.ChildEnvironment, extraArguments: extraArguments);
        runner.MessageReceived += message => messages.Add(message);
        runner.Start();

        bool sawExit = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (!messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
            {
                break;
            }

            if (message.Kind == ChildMessageKind.Prompt)
            {
                string reply = message.Caller switch { "Read-Answer" => "yes", "Wait-Owner" => string.Empty, _ => "y" };
                runner.ReplyFromOwnerClick(message.Seq, reply);
            }
            else if (message.Kind == ChildMessageKind.Exit)
            {
                sawExit = true;
                break;
            }
            else if (message.Kind == ChildMessageKind.Crash)
            {
                Assert.Fail("The sandboxed run crashed with an impossible -ExePath: " + message.Text);
            }
        }

        Assert.IsTrue(sawExit, "No exit message arrived with an impossible -ExePath.");
        Assert.IsTrue(runner.WaitForExit(TimeSpan.FromSeconds(10)));

        string resultPath = Path.Combine(runRoot, "01-a2dp-oneshot", "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, "01-a2dp-oneshot");
        Assert.IsNotNull(result, "result.json did not parse with an impossible -ExePath: " + failure);
        Assert.AreEqual("pass", result!.Overall);
    }

    private static string RunScript(string host, string scriptPath)
    {
        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(
            host, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath });

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The probe script did not finish within " + ProbeTimeout + ".");
        }

        process.WaitForExit();
        lock (output)
        {
            lock (errors)
            {
                if (errors.Length > 0)
                {
                    Assert.Fail("The probe script wrote to its error stream: " + errors);
                }

                return output.ToString();
            }
        }
    }
}
