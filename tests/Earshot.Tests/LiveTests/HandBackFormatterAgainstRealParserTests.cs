using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// tools\live-tests\selftest\Fakes.psm1 hand-writes the Hand-back log lines that
// 17-HandBackOnShutdown.ps1 and 18-HandBackOnSleep.ps1 parse. A fixture that quietly drifts from
// what the real HandBackText formats would let every self-test case for those two scripts pass
// while the real application never satisfies a single criterion: the self-test only proves the
// scripts against lines this repository typed by hand.
//
// This is the other half of "keep one execution of the real boundary": RealHandBackLines
// below calls the real, unfaked HandBackText for every line shape 17 and 18 read, and
// Test-RealHandBackLines.ps1 (tools\live-tests\selftest) reads them back with the real
// Get-EarshotLogLines and reruns the same -match checks those two scripts run, in a real
// PowerShell 5.1 process through WindowsPowerShellHost. HandBackFixturesInFakesPsm1MatchTheRealFormatter
// in LiveTestFieldTests.cs pins the same shapes against the literal text in Fakes.psm1.
internal static class RealHandBackLines
{
    private static readonly DateTimeOffset ShutdownStartedAt = new(2026, 9, 22, 1, 31, 42, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ShutdownBlockSentAt = new(2026, 9, 22, 1, 31, 42, 400, TimeSpan.Zero);
    private static readonly DateTimeOffset SleepStartedAt = new(2026, 9, 22, 1, 41, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SleepBlockSentAt = new(2026, 9, 22, 1, 41, 0, 300, TimeSpan.Zero);

    // Message text only, exactly as HandBackText returns it (no timestamp or level prefix): the
    // shape Fakes.psm1 pins in its LogFixtures table and its handback-cut-short branch. PinText is
    // what LiveTestFieldTests checks Fakes.psm1 for: equal to Text except for the shutdown cut-short
    // shape, whose "block was sent at" timestamp Fakes.psm1 generates from the run clock rather than
    // holding as a literal, so the pin stops at the fixed prefix.
    public static readonly (string Label, string Text, string PinText)[] Messages = FillPinText(BuildMessages());

    private static (string, string, string)[] BuildMessages()
    {
        string shutdownCutShort = HandBackText.CutShort(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(4000), ["block"], ShutdownBlockSentAt);
        const string shutdownCutShortPin = "Hand-back (shutdown): cut short at 4000 ms; still running: block; block was sent at";

        return
        [
            ("shutdown started",
                HandBackText.Started(HandBackTrigger.SessionEnd, ShutdownStartedAt, "WM_ENDSESSION, shutdown or restart", RenderState.Active, BlockState.Allowed, streamingHeld: false, blockAtBoot: true), ""),
            ("shutdown disconnect confirmed 37ms",
                HandBackText.Disconnect(HandBackTrigger.SessionEnd, "S_OK", confirmed: true, TimeSpan.FromMilliseconds(37)), ""),
            ("shutdown disconnect confirmed 20ms",
                HandBackText.Disconnect(HandBackTrigger.SessionEnd, "S_OK", confirmed: true, TimeSpan.FromMilliseconds(20)), ""),
            ("shutdown block sent at",
                HandBackText.BlockSentAt(HandBackTrigger.SessionEnd, ShutdownBlockSentAt), ""),
            ("shutdown finished",
                HandBackText.Finished(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(303), "confirmed", "Success"), ""),
            ("shutdown cut short, block sent",
                shutdownCutShort, shutdownCutShortPin),
            ("sleep started",
                HandBackText.Started(HandBackTrigger.Suspend, SleepStartedAt, "PBT_APMSUSPEND", RenderState.Active, BlockState.Allowed, streamingHeld: false, blockAtBoot: true), ""),
            ("sleep disconnect confirmed 22ms",
                HandBackText.Disconnect(HandBackTrigger.Suspend, "S_OK", confirmed: true, TimeSpan.FromMilliseconds(22)), ""),
            ("sleep disconnect not confirmed 750ms",
                HandBackText.Disconnect(HandBackTrigger.Suspend, "S_OK", confirmed: false, TimeSpan.FromMilliseconds(750)), ""),
            ("sleep block sent at",
                HandBackText.BlockSentAt(HandBackTrigger.Suspend, SleepBlockSentAt), ""),
            ("sleep finished",
                HandBackText.Finished(HandBackTrigger.Suspend, TimeSpan.FromMilliseconds(280), "confirmed", "Success"), ""),
            ("sleep cut short, block not sent",
                HandBackText.CutShort(HandBackTrigger.Suspend, TimeSpan.FromMilliseconds(1500), ["disconnect", "block"], blockSentAt: null), ""),
            ("suspend logged",
                "WM_POWERBROADCAST received: Suspend (wParam 0x4).", ""),
            ("resume automatic logged",
                "WM_POWERBROADCAST received: ResumeAutomatic (wParam 0x12).", ""),
            ("resume check blocked",
                HandBackText.ResumeBlocked(), ""),
        ];
    }

    private static (string, string, string)[] FillPinText((string Label, string Text, string PinText)[] rows) =>
        [.. rows.Select(r => (r.Label, r.Text, r.PinText.Length == 0 ? r.Text : r.PinText))];

    // Full log lines, in the real single-space FileLog.AppendEntry shape (timestamp, one space,
    // level, one space, message), for the process-boundary run.
    public static IEnumerable<string> FullLines()
    {
        DateTime stamp = new(2026, 9, 22, 1, 31, 42, DateTimeKind.Utc);
        foreach ((string _, string text, string _) in Messages)
        {
            stamp = stamp.AddSeconds(1);
            string level = text.Contains("cut short", StringComparison.Ordinal) ? "WARN" : "INFO";
            yield return stamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + level + " " + text;
        }
    }
}

[TestClass]
public sealed class HandBackFormatterAgainstRealParserTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void TheScriptsRealPatternsRecogniseEveryLineTheRealFormatterWrites()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the real formatter's lines " +
                "were never run through the scripts' real parser and this settles nothing about it.");
        }

        string root = RepositoryRoot();
        string script = Path.Combine(root, "tools", "live-tests", "selftest", "Test-RealHandBackLines.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealHandBackLines.ps1 was not found at " + script + ".");

        string linesFile = Path.Combine(Path.GetTempPath(), "earshot-handback-lines-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(linesFile, JsonSerializer.Serialize(RealHandBackLines.FullLines().ToArray()));

            (int exit, string output, string errors) = RunScript(host, script, linesFile, root);

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(output),
                "Test-RealHandBackLines.ps1 printed nothing. Host " + host + ", exit " +
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
                "The scripts' own patterns did not recognise the real formatter's lines:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems) + Environment.NewLine + output);

            Assert.IsTrue(
                result.GetProperty("ok").GetBoolean(),
                "Test-RealHandBackLines.ps1 reported ok: false with no problem listed, which should not happen." +
                Environment.NewLine + output);

            Assert.IsGreaterThan(0, result.GetProperty("checked").GetInt32(), "No check ran, which cannot be right.");
            Assert.AreEqual(0, exit, "Test-RealHandBackLines.ps1 exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
        }
        finally
        {
            File.Delete(linesFile);
        }
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
                "Test-RealHandBackLines.ps1 did not print a JSON result: " + error.Message + Environment.NewLine + output);
        }
    }

    private static (int Exit, string Output, string Errors) RunScript(string host, string script, string linesFile, string root)
    {
        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Root", root, "-LinesFile", linesFile,
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
            Assert.Fail("Test-RealHandBackLines.ps1 did not finish within " + RunTimeout + ".");
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
