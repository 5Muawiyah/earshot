using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The self-test's fake log never holds a NUL byte, so it cannot show what the live-test log reader does with the first line
// after a power loss. This runs tools\live-tests\selftest\Test-RealLogLines.ps1, which writes such a log (a run of NULs in front
// of an entry) and reads it through the real Get-EarshotLogLines in a real Windows PowerShell 5.1 process.
[TestClass]
public sealed class RealLogLinesSelfTestTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void ALogLineAfterANulRunKeepsItsTimeAndIsReadWithoutTheRun()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so the log reader was not run and this settles nothing about it.");
        }

        string root = RepositoryRoot();
        string script = Path.Combine(root, "tools", "live-tests", "selftest", "Test-RealLogLines.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealLogLines.ps1 was not found at " + script + ".");

        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Root", root,
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
            Assert.Fail("Test-RealLogLines.ps1 did not finish within " + RunTimeout + ".");
        }

        process.WaitForExit();
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(text), "Test-RealLogLines.ps1 printed nothing. Exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + errors);
        using JsonDocument document = JsonDocument.Parse(text.Trim());
        JsonElement result = document.RootElement;
        var problems = new List<string>();
        if (result.TryGetProperty("problems", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement problem in listed.EnumerateArray())
            {
                problems.Add(problem.ToString());
            }
        }

        Assert.IsEmpty(problems, "The log reader did not read a log after a power loss as it should:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.IsGreaterThan(0, result.GetProperty("checked").GetInt32(), "Nothing was checked.");
        Assert.AreEqual(0, process.ExitCode);
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
