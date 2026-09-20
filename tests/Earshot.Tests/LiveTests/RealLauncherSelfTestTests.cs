using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// LiveTestSelfTestTests runs every shipped live test script against a fake machine, which is
// deliberate: Run-OneHalf.ps1 replaces Invoke-Earshot with a stub for every one of those cases,
// so the real function, the one built around
// "Start-Process -FilePath ... -NoNewWindow -PassThru -RedirectStandardOutput", was never
// executed by anything until the owner's first live sitting. There, every step came back with
// exitCode null: under Windows PowerShell 5.1, Start-Process -PassThru only fills in ExitCode
// for a process whose handle was read while it was still running. Commit ebb6d4c added
// "$null = $process.Handle" and a guard that reports an unreadable code instead of scoring it,
// with no committed test proving either.
//
// This test runs tools\live-tests\selftest\Test-RealLauncher.ps1, which calls the real, unfaked
// Invoke-Earshot against %SystemRoot%\System32\cmd.exe instead of Earshot.exe and checks the
// exit codes it records. It is a separate step from LiveTestSelfTestTests on purpose: that
// self-test's counts (scripts, halves, cases) describe the sweep over the shipped live test
// scripts with Invoke-Earshot faked, and this proves the one function that sweep never calls
// for real. A missing or failing script here fails this test outright, not just Inconclusive,
// except where Windows PowerShell 5.1 itself is not installed, the same carve-out
// LiveTestSelfTestTests uses.
[TestClass]
public sealed class RealLauncherSelfTestTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void InvokeEarshotRecordsTheRealProcessExitCode()
    {
        string host = WindowsPowerShell51Path();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the real launcher " +
                "was not run and this settles nothing about it.");
        }

        string script = Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Test-RealLauncher.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealLauncher.ps1 was not found at " + script + ".");

        (int exit, string output, string errors) = RunScript(host, script);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(output),
            "Test-RealLauncher.ps1 printed nothing. Host " + host + ", exit " +
            exit.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + errors);

        JsonElement result = ReadLastJsonObject(output);

        var problems = new List<string>();
        if (result.TryGetProperty("problems", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement problem in listed.EnumerateArray())
            {
                problems.Add(problem.ToString());
            }
        }

        Assert.IsEmpty(
            problems,
            "The real launcher did not record the exit codes it should have:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems) + Environment.NewLine + output);

        Assert.IsTrue(
            result.GetProperty("ok").GetBoolean(),
            "Test-RealLauncher.ps1 reported ok: false with no problem listed, which should not happen." +
            Environment.NewLine + output);

        JsonElement exitCodes = result.GetProperty("exitCodes");
        Assert.AreEqual(7, exitCodes.GetProperty("exit7").GetInt32(), "cmd.exe /c exit 7 was not recorded as exitCode 7.");
        Assert.AreEqual(0, exitCodes.GetProperty("exit0").GetInt32(), "cmd.exe /c exit 0 was not recorded as exitCode 0.");
        Assert.AreEqual(0, exit, "Test-RealLauncher.ps1 exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
    }

    // The runner prints one JSON object last. Anything before it is a warning from PowerShell,
    // which is worth keeping in the failure text but is not the result.
    private static JsonElement ReadLastJsonObject(string output)
    {
        int start = output.LastIndexOf("\n{", StringComparison.Ordinal);
        string json = start < 0 ? output.Trim() : output[(start + 1)..].Trim();
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new AssertFailedException(
                "Test-RealLauncher.ps1 did not print a JSON result: " + error.Message + Environment.NewLine + output);
        }
    }

    private static (int Exit, string Output, string Errors) RunScript(string host, string script)
    {
        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Root", RepositoryRoot(),
        });

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(errors, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Test-RealLauncher.ps1 did not finish within " + RunTimeout + ".");
        }

        process.WaitForExit();
        lock (output)
        {
            lock (errors)
            {
                return (process.ExitCode, output.ToString(), errors.ToString());
            }
        }
    }

    private static void Append(StringBuilder text, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (text)
        {
            text.AppendLine(line);
        }
    }

    // Where Windows PowerShell 5.1 lives on every Windows install. Returned whether or not it is
    // there: the caller decides what an absent host means.
    private static string WindowsPowerShell51Path() => WindowsPowerShellHost.Path51();

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
