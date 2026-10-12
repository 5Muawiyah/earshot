using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// The guide tells an AI how to run Earshot.exe and read its exit code and output. Earshot.exe is a Windows-subsystem program,
// so a plain call from PowerShell does not wait for it. These tests take the code blocks out of docs\ai-guide.md as written,
// point them at the test build's Earshot.exe and run them in the real shells, so the documented form is the one proved.
[TestClass]
public sealed class AiGuideCommandsTests
{
    private const string InstalledExeLiteral = "'C:\\Program Files\\Earshot\\Earshot.exe'";

    private static string GuideText()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "docs", "ai-guide.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException("docs\\ai-guide.md was not found above " + AppContext.BaseDirectory);
    }

    private static string BlockContaining(string needle)
    {
        MatchCollection blocks = Regex.Matches(GuideText(), "```\n(.*?)\n```", RegexOptions.Singleline);
        string[] hits = blocks.Select(b => b.Groups[1].Value).Where(b => b.Contains(needle, StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, hits, "The guide must hold exactly one code block with " + needle + " in it.");
        return hits[0];
    }

    private static string TestExe()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        return exe;
    }

    // Runs a block of the guide in a shell with the installed path swapped for the test build's program, a data root of its
    // own, and a TEMP of its own (the block writes there). Returns the shell's standard output, one line per element.
    private static string[] RunBlock(ShellKind shell, string block, TempFolder temp)
    {
        string host = PwshHost.Require(shell);
        Assert.Contains(InstalledExeLiteral, block, "The block no longer names the installed program the way the test swaps it.");
        string script = block.Replace(InstalledExeLiteral, "'" + TestExe() + "'", StringComparison.Ordinal);
        string scriptFile = temp.File("guide-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptFile, script);
        Directory.CreateDirectory(temp.File("t"));

        ProcessStartInfo info = PwshHost.CreateStartInfo(shell, host, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptFile]);
        info.Environment["EARSHOT_SAFE_MODE"] = "1";
        info.Environment["EARSHOT_DATA_ROOT"] = temp.File("data");
        info.Environment["TEMP"] = temp.File("t");
        info.Environment["TMP"] = temp.File("t");
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(120)), "The shell did not end.");
        Assert.AreEqual(0, process.ExitCode, "The shell failed: " + error.Result + output);
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheStartProcessFormInTheGuideWaitsAndGivesTheExitCodeAndTheOutput(ShellKind shell)
    {
        using var temp = new TempFolder();
        string block = BlockContaining("Start-Process");

        string[] lines = RunBlock(shell, block, temp);

        Assert.AreEqual("0", lines[0], "The exit code is the first line the block prints.");
        Assert.IsTrue(lines.Any(l => l.StartsWith("low-battery-percent = 20", StringComparison.Ordinal)), "The settings list was not read back: " + string.Join("|", lines));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheStartProcessFormGivesANonZeroExitCodeForARefusedCommand(ShellKind shell)
    {
        using var temp = new TempFolder();
        string block = BlockContaining("Start-Process").Replace("'settings', 'list'", "'settings', 'get', 'no-such-setting'", StringComparison.Ordinal);

        string[] lines = RunBlock(shell, block, temp);

        Assert.AreEqual("64", lines[0], "An unknown setting name is a usage error, exit code 64.");
        Assert.IsTrue(lines.Any(l => l.Contains("There is no setting called", StringComparison.Ordinal)), string.Join("|", lines));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ThePipedFormInTheGuideWaitsAndGivesTheExitCodeInLastExitCode(ShellKind shell)
    {
        using var temp = new TempFolder();
        string block = BlockContaining("Out-String");

        string[] lines = RunBlock(shell, block, temp);

        Assert.AreEqual("20", lines[0].Trim(), string.Join("|", lines));
        Assert.AreEqual("0", lines[^1].Trim(), "$LASTEXITCODE is the last line.");
    }

    [TestMethod]
    public void TheGuideNamesTheOnlyParametersAnAiMayPassAndWhatDoctorWrites()
    {
        string guide = Regex.Replace(GuideText(), @"\s+", " ");

        StringAssert.Contains(guide, "Pass only -Action, -DryRun and -RemoveSettings");
        StringAssert.Contains(guide, "It reads only: it writes its own log and a temporary download");
        Assert.IsFalse(guide.Contains("It changes nothing", StringComparison.Ordinal), "Doctor writes a log and a download, so it does not change nothing.");
        StringAssert.Contains(guide, "If you run Setup.exe yourself, check it against the SHA-256 in the release notes first");
        Assert.IsFalse(guide.Contains("published SHA-256 is checked", StringComparison.Ordinal), "Nothing in the script checks Setup.exe, so the guide must not say it does.");
    }
}
