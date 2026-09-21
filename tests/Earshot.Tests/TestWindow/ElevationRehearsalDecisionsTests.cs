using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// S9: "Prove everything short of it against the stub." Nothing here starts a process with -Verb
// RunAs, raises a Windows prompt, or calls the real Invoke-EarshotElevated: it dot-sources
// tools\live-tests\gui\ElevationRehearsalDecisions.ps1 (the part of Test-ElevatedLaunch.ps1 that
// does not itself elevate) with real Windows PowerShell 5.1 and synthetic step data standing in
// for what a real approve/decline/H2-unreadable-exit-code round would have recorded.
[TestClass]
public sealed class ElevationRehearsalDecisionsTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void ApprovedWithTheRealExitCodeSevenIsAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-ApprovedExitCodeOutcome -ReturnValue ([pscustomobject]@{ exitCode = 7 }) -LastStep ([pscustomobject]@{ ran = $true; exitCode = 7; error = $null })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("pass", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void ApprovedButTheExitCodeCouldNotBeReadIsInconclusiveNeverAPass()
    {
        // design.md note H2: ran true, exitCode null.
        JsonElement result = RunDriver("""
            $r = Get-ApprovedExitCodeOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $true; exitCode = $null; error = 'The elevated run started, but PowerShell did not give its exit code, so this step says nothing either way.' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("inconclusive", result.GetProperty("outcome").GetString());
        StringAssert.Contains(result.GetProperty("detail").GetString(), "note H2");
    }

    [TestMethod]
    public void ApprovedRoundActuallyDeclinedIsAFailNeverAnInconclusiveOrAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-ApprovedExitCodeOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = 'skipped at the owner request' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("fail", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void ApprovedWithNoStepAtAllIsAFail()
    {
        JsonElement result = RunDriver("""
            $r = Get-ApprovedExitCodeOutcome -ReturnValue $null -LastStep $null
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("fail", result.GetProperty("outcome").GetString());
    }

    // M5: only an error naming Windows' own cancellation code (1223, ERROR_CANCELLED) is a
    // recorded decline. LiveTest.psm1's own catch comment names it: "a declined prompt reports
    // 1223".
    [TestMethod]
    public void DeclinedWithTheReal1223ShapeIsAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = 'The elevated run could not be started (a declined prompt reports 1223): System.ComponentModel.Win32Exception: The operation was canceled by the user' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("pass", result.GetProperty("outcome").GetString());
    }

    // M5's own two named cases: neither ever reaches Start-Process, so neither can be a Windows
    // decline, whatever ElevationRehearsalDecisions.ps1 used to think of their shape.
    [TestMethod]
    public void ANoInTheWindowsOwnStepIsInconclusiveNeverAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = 'skipped at the owner request' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("inconclusive", result.GetProperty("outcome").GetString());
        StringAssert.Contains(result.GetProperty("detail").GetString(), "1223");
    }

    [TestMethod]
    public void ATimeoutIsInconclusiveNeverAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = 'It did not finish within 600 s.' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("inconclusive", result.GetProperty("outcome").GetString());
    }

    // A ran=false error that names neither 1223 nor either named exemption (some other launch
    // failure entirely) is also inconclusive: it does not settle whether Windows' own box ever
    // appeared either.
    [TestMethod]
    public void SomeOtherLaunchFailureIsInconclusiveNeverAPass()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = 'The system cannot find the file specified' })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("inconclusive", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void DeclinedRoundActuallyApprovedIsAFail()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue ([pscustomobject]@{ exitCode = 7 }) -LastStep ([pscustomobject]@{ ran = $true; exitCode = 7; error = $null })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("fail", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void DeclinedWithNoErrorRecordedIsAFailTheShapeMattersNotJustReturningNull()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep ([pscustomobject]@{ ran = $false; exitCode = $null; error = $null })
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("fail", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void DeclinedWithNoStepAtAllIsAFail()
    {
        JsonElement result = RunDriver("""
            $r = Get-DeclinedRecordedOutcome -ReturnValue $null -LastStep $null
            $r | ConvertTo-Json -Compress
            """);
        Assert.AreEqual("fail", result.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void TheWrittenResultJsonParsesUnderEvidenceStoresOwnFailClosedRulesAndReadsAsAPass()
    {
        using var root = new TempFolder();
        string folder = Path.Combine(root.Path, "elevated-launch-rehearsal");
        RunDriver("""
            $run = New-ElevationRehearsalRun -Folder '__FOLDER__'
            Add-Criterion -Run $run -Id 'approved-exit-code' -Criterion 'c1' -Outcome 'pass' -Detail 'd1'
            Add-Criterion -Run $run -Id 'declined-recorded' -Criterion 'c2' -Outcome 'pass' -Detail 'd2'
            Add-Finding -Run $run -Name 'promptCameToFront' -Value 'yes' -Detail ''
            Add-Finding -Run $run -Name 'leftAtRest' -Value 'not-applicable' -Detail 'This check starts cmd.exe only.'
            $overall = Write-ElevationRehearsalResult -Run $run
            Write-Host ('OVERALL=' + $overall)
            """.Replace("__FOLDER__", folder.Replace("'", "''")), expectJson: false);

        string resultPath = Path.Combine(folder, "result.json");
        Assert.IsTrue(File.Exists(resultPath), "result.json was not written to the expected subfolder.");

        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, "elevated-launch-rehearsal");
        Assert.IsNotNull(result, "result.json did not parse under EvidenceStore's own fail-closed rules: " + failure);
        Assert.AreEqual("pass", result!.Overall);
        Assert.AreEqual("not-applicable", result.LeftAtRest);
        Assert.IsTrue(result.Findings.Any(f => f.Name == "promptCameToFront" && f.Value == "yes"));
    }

    [TestMethod]
    public void AnyFailingCriterionMakesTheWholeCheckFail()
    {
        using var root = new TempFolder();
        string folder = Path.Combine(root.Path, "elevated-launch-rehearsal");
        RunDriver("""
            $run = New-ElevationRehearsalRun -Folder '__FOLDER__'
            Add-Criterion -Run $run -Id 'approved-exit-code' -Criterion 'c1' -Outcome 'fail' -Detail 'd1'
            Add-Criterion -Run $run -Id 'declined-recorded' -Criterion 'c2' -Outcome 'pass' -Detail 'd2'
            Add-Finding -Run $run -Name 'leftAtRest' -Value 'not-applicable' -Detail ''
            $overall = Write-ElevationRehearsalResult -Run $run
            """.Replace("__FOLDER__", folder.Replace("'", "''")), expectJson: false);

        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(Path.Combine(folder, "result.json"), "elevated-launch-rehearsal");
        Assert.IsNotNull(result, failure);
        Assert.AreEqual("fail", result!.Overall);
    }

    private static JsonElement RunDriver(string body, bool expectJson = true)
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string decisionsPath = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "gui", "ElevationRehearsalDecisions.ps1");
        Assert.IsTrue(File.Exists(decisionsPath), "fixture missing: " + decisionsPath);

        string modulePath = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "LiveTest.psm1");
        string scriptPath = Path.Combine(Path.GetTempPath(), "earshot-elevation-decisions-" + Guid.NewGuid().ToString("N") + ".ps1");
        string fullScript = "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
            "Microsoft.PowerShell.Core\\Import-Module '" + modulePath.Replace("'", "''") + "' -Force" + Environment.NewLine +
            ". '" + decisionsPath.Replace("'", "''") + "'" + Environment.NewLine + body;
        File.WriteAllText(scriptPath, fullScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            ProcessStartInfo info = PowerShell51.CreateStartInfo(host, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath });
            var output = new StringBuilder();
            var errors = new StringBuilder();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The driver did not finish within " + RunTimeout + ".");
            }

            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, "driver failed: " + errors + Environment.NewLine + output);

            if (!expectJson)
            {
                return default;
            }

            using JsonDocument document = JsonDocument.Parse(output.ToString());
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}
