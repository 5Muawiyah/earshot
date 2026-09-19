using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// LiveTestScriptTests reads the live test scripts. This one runs them.
//
// Reading is not enough, and twice it has not been. The scripts run under
// Set-StrictMode -Version 2.0, and PowerShell unrolls whatever a function writes to the
// pipeline: a helper ending "return $found" hands the caller $null when nothing matched and a
// bare string when one thing did, and .Count on either throws PropertyNotFoundStrict. The throw
// lands in the script's own catch, which records "run: fail" and abandons every criterion after
// it. It fires hardest on the success path, where the honest answer is "no matching line". A
// script in that state parses, reads correctly, passes every static guard, and settles nothing.
//
// So tools\live-tests\selftest runs every shipped script, and both halves of every resumable
// one, in a real powershell.exe 5.1 process against a fake machine. Only the device-touching and
// owner-prompting helpers are replaced; Get-EarshotLogLines, Get-DiagEvidence, Copy-AppEvidence,
// Read-KsEvidence, Read-EarshotJsonFile, Add-Criterion and Complete-LiveTestRun all run for real.
// Three sets of fake inputs are used, holding 0, 1 and 2 matching lines and list items, because
// those are the three shapes a returned list takes.
//
// Nothing here touches a device, registers a task, elevates, or starts Earshot.exe: the runner
// replaces Start-Process inside each child with one that throws, and every path is redirected
// into a sandbox under %TEMP%.
[TestClass]
public sealed class LiveTestSelfTestTests
{
    // 66 short-lived PowerShell processes. Generous, because a machine under load is not a defect.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

    // Every shipped script, and the halves the self-test has to cover. A script or a half added
    // to tools\live-tests without being added here would otherwise be run by nothing.
    private const int ExpectedScripts = 16;
    private const int ExpectedHalves = 22;
    private const int ExpectedCases = 3;

    [TestMethod]
    public void EveryShippedLiveTestRunsToItsEndAgainstFakeInputs()
    {
        // The shipped scripts are run with Windows PowerShell 5.1 on the machine, so the self-test
        // runs them in the same host. Where that host is not installed the scripts are not covered,
        // and the honest record of that is inconclusive rather than a pass. The path is named, so a
        // machine that was never able to run this is never read as having settled it.
        string host = WindowsPowerShell51Path();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the shipped live test " +
                "scripts were not run and this settles nothing about them.");
        }

        string workRoot = Path.Combine(Path.GetTempPath(), "earshot-live-selftest-" + Guid.NewGuid().ToString("N"));
        (int exit, string output, string errors) = RunSelfTest(workRoot, host);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(output),
            "The self-test printed nothing. Host " + host + ", exit " +
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
            "Running the live test scripts against the fake machine found:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems) + Environment.NewLine +
            "The evidence of every case that failed is under " + workRoot + ".");

        Assert.IsTrue(
            result.GetProperty("ok").GetBoolean(),
            "The self-test reported ok: false with no problem listed, which should not happen." + Environment.NewLine + output);

        // The counts, so a script or a half that stops being covered is a failure rather than a
        // quieter run. The runner names any shipped script it has no row for, which catches the
        // other direction.
        Assert.AreEqual(ExpectedScripts, result.GetProperty("scripts").GetInt32(), "The self-test ran a different number of scripts than expected.");
        Assert.AreEqual(ExpectedHalves, result.GetProperty("halves").GetInt32(), "The self-test ran a different number of halves than expected.");
        Assert.AreEqual(ExpectedHalves * ExpectedCases, result.GetProperty("runs").GetInt32(), "The self-test ran a different number of cases than expected.");
        Assert.AreEqual(0, exit, "The self-test exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
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
                "The self-test did not print a JSON result: " + error.Message + Environment.NewLine + output);
        }
    }

    private static (int Exit, string Output, string Errors) RunSelfTest(string workRoot, string host)
    {
        string runner = Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Invoke-SelfTest.ps1");
        Assert.IsTrue(File.Exists(runner), "The self-test runner was not found at " + runner + ".");

        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", runner,
            "-Root", RepositoryRoot(), "-WorkRoot", workRoot,
        })
        {
            info.ArgumentList.Add(argument);
        }

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
            Assert.Fail("The live test self-test did not finish within " + RunTimeout + ".");
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
    // there: the caller decides what an absent host means, which used to be a bare "powershell.exe"
    // handed to a child process and an obscure failure some way further in.
    private static string WindowsPowerShell51Path()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
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
