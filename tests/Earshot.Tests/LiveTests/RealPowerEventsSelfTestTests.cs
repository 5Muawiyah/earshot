using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// tools\live-tests\selftest replaces Get-PowerEvents with a fake for every case it runs, the same
// reason RealLauncherSelfTestTests exists for Invoke-Earshot: a helper the self-test never calls
// for real has never actually been proven against Get-WinEvent and a real System event log,
// FilterHashtable syntax included.
//
// This test runs tools\live-tests\selftest\Test-RealPowerEvents.ps1, which calls the real,
// unfaked Get-PowerEvents for the last 24 hours and checks that it returns a list (possibly
// empty) and that the read itself did not fail. It is a separate step from
// LiveTestSelfTestTests on purpose, for the same reason RealLauncherSelfTestTests is: that
// self-test's counts describe the sweep over the shipped live test scripts with Get-PowerEvents
// faked, and this proves the one function that sweep never calls for real.
[TestClass]
public sealed class RealPowerEventsSelfTestTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void GetPowerEventsReadsTheRealSystemEventLog()
    {
        string host = WindowsPowerShell51Path();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the real event log read " +
                "was not run and this settles nothing about it.");
        }

        string script = Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Test-RealPowerEvents.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealPowerEvents.ps1 was not found at " + script + ".");

        (int exit, string output, string errors) = RunScript(host, script);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(output),
            "Test-RealPowerEvents.ps1 printed nothing. Host " + host + ", exit " +
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
            "The real event log read did not go as it should have:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems) + Environment.NewLine + output);

        Assert.IsTrue(
            result.GetProperty("ok").GetBoolean(),
            "Test-RealPowerEvents.ps1 reported ok: false with no problem listed, which should not happen." +
            Environment.NewLine + output);

        Assert.IsGreaterThanOrEqualTo(0, result.GetProperty("eventCount").GetInt32(), "eventCount must never be negative.");
        Assert.AreEqual(0, exit, "Test-RealPowerEvents.ps1 exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
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
                "Test-RealPowerEvents.ps1 did not print a JSON result: " + error.Message + Environment.NewLine + output);
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
            Assert.Fail("Test-RealPowerEvents.ps1 did not finish within " + RunTimeout + ".");
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
