using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Data\tests.json's own maxSilenceSeconds for rows 03 and 13 is derived from those two scripts'
// own default wait (03-AllowPages.ps1's -WatchSeconds, 13-GraceWindow.ps1's -WatchMinutes), not
// pinned to it: nothing failed if one of those defaults grew past the manifest's own allowance,
// which would let the silence watchdog fire mid-test on a script still legitimately waiting.
// Export-SilenceDefaults.ps1 reads both defaults out of the parsed source (AST, the same
// technique Export-SelfTestPlan.ps1 already uses), so this is checked against the real scripts,
// not a second copy of the same two numbers.
[TestClass]
public sealed class SilenceAllowancePinnedToScriptDefaultsTests
{
    [TestMethod]
    public void TheManifestsAllowedSilenceForZero3AndOne3CoversTheScriptsOwnDefaultWait()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string repoRoot = RepositoryLocator.RepositoryRoot();
        (int watchSeconds, int watchMinutes) = RunExporter(host, repoRoot);

        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        ManifestRow row03 = rows.Single(r => r.Number == "03");
        ManifestRow row13 = rows.Single(r => r.Number == "13");

        Assert.IsGreaterThanOrEqualTo(watchSeconds, row03.MaxSilenceSeconds,
            "row 03's maxSilenceSeconds (" + row03.MaxSilenceSeconds + ") no longer covers 03-AllowPages.ps1's own -WatchSeconds default (" + watchSeconds + ").");

        int watchMinutesAsSeconds = watchMinutes * 60;
        Assert.IsGreaterThanOrEqualTo(watchMinutesAsSeconds, row13.MaxSilenceSeconds,
            "row 13's maxSilenceSeconds (" + row13.MaxSilenceSeconds + ") no longer covers 13-GraceWindow.ps1's own -WatchMinutes default (" + watchMinutes + " min).");
    }

    private static (int WatchSeconds, int WatchMinutes) RunExporter(string host, string repoRoot)
    {
        string scriptPath = Path.Combine(repoRoot, "tools", "live-tests", "gui", "Export-SilenceDefaults.ps1");
        Assert.IsTrue(File.Exists(scriptPath), "Export-SilenceDefaults.ps1 was not found at " + scriptPath);

        ProcessStartInfo info = PowerShell51.CreateStartInfo(host, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath });
        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Assert.IsTrue(process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds), "Export-SilenceDefaults.ps1 did not finish within 60 s.");
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "Export-SilenceDefaults.ps1 failed: " + errors + Environment.NewLine + output);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return (document.RootElement.GetProperty("watchSeconds").GetInt32(), document.RootElement.GetProperty("watchMinutes").GetInt32());
    }
}
