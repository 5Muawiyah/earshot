using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// tools\live-tests\selftest replaces Test-EarshotRunning with a fake for every case it runs, the same reason
// RealPowerEventsSelfTestTests exists for Get-PowerEvents: a helper the self-test never calls for real has never
// been proven against Get-Process on this machine.
//
// This runs tools\live-tests\selftest\Test-RealEarshotRunning.ps1, which calls the real, unfaked helper and checks
// that it answers yes or no and agrees with an independent read of the process list (tasklist.exe, by its session
// column), and proves the yes answer with a decoy process named Earshot.exe (a copy of ping.exe, stopped again). It never
// starts the tray, and it says whether the session-0 branch was exercised.
[TestClass]
public sealed class RealEarshotRunningSelfTestTests
{
    public TestContext? TestContext { get; set; }

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void TestEarshotRunningReadsTheRealProcessList()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the real process list read " +
                "was not run and this settles nothing about it.");
        }

        string script = Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Test-RealEarshotRunning.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealEarshotRunning.ps1 was not found at " + script + ".");

        (int exit, string output, string errors) = RunScript(host, script);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(output),
            "Test-RealEarshotRunning.ps1 printed nothing. Host " + host + ", exit " +
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
            "The real process list read did not go as it should have:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems) + Environment.NewLine + output);
        Assert.IsTrue(result.GetProperty("ok").GetBoolean(), "Test-RealEarshotRunning.ps1 reported ok: false with no problem listed." + Environment.NewLine + output);
        string? running = result.GetProperty("running").GetString();
        Assert.IsTrue(running is "yes" or "no", "Test-EarshotRunning did not answer yes or no: " + running + Environment.NewLine + output);
        Assert.AreEqual(running, result.GetProperty("independentRunning").GetString(), "The helper and the independent read of tasklist.exe disagree." + Environment.NewLine + output);
        Assert.AreEqual("yes", result.GetProperty("decoyRunning").GetString(), "A process named Earshot.exe in a user session was not seen as the tray." + Environment.NewLine + output);
        string? session0 = result.GetProperty("session0Branch").GetString();
        Assert.IsTrue(session0 is not null && (session0.StartsWith("exercised", StringComparison.Ordinal) || session0.StartsWith("unproved", StringComparison.Ordinal)), output);
        TestContext?.WriteLine("Test-EarshotRunning: " + running + "; session 0 branch " + session0);
        Assert.AreEqual(0, exit, "Test-RealEarshotRunning.ps1 exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
    }

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
                "Test-RealEarshotRunning.ps1 did not print a JSON result: " + error.Message + Environment.NewLine + output);
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
            Assert.Fail("Test-RealEarshotRunning.ps1 did not finish within " + RunTimeout + ".");
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
