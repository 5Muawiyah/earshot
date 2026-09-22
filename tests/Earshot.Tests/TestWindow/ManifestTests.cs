using System.Diagnostics;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Scripts equal the 18 shipped; TestIds equal the
// -TestId literals; halves sum to 25; markers agree with selftest\expectations.psd1, read
// through a real Windows PowerShell 5.1 child exactly as the harness itself reads it
// (Import-PowerShellDataFile), never re-typed as a C# literal.
[TestClass]
public sealed class ManifestTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    private static IReadOnlyList<ManifestRow> LoadManifest() =>
        Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));

    [TestMethod]
    public void EighteenRowsSummingToTwentyFiveHalves()
    {
        IReadOnlyList<ManifestRow> rows = LoadManifest();
        Assert.AreEqual(Manifest.ExpectedRowCount, rows.Count);
        Assert.AreEqual(Manifest.ExpectedHalfSum, rows.Sum(r => r.Halves));
    }

    [TestMethod]
    public void ScriptsEqualTheEighteenShipped()
    {
        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        string[] shipped = Directory.EnumerateFiles(liveTestsRoot, "*.ps1", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Cast<string>()
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{2}-"))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        IReadOnlyList<ManifestRow> rows = LoadManifest();
        string[] manifestScripts = rows.Select(r => r.Script).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(shipped, manifestScripts);
    }

    [TestMethod]
    public void TestIdsMatchTheLiteralEachScriptPassesToNewLiveTestRun()
    {
        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        foreach (ManifestRow row in LoadManifest())
        {
            string text = File.ReadAllText(Path.Combine(liveTestsRoot, row.Script));
            if (row.Variants is { Count: > 0 } variants)
            {
                StringAssert.Contains(text, "-TestId ('" + row.TestId + "-v' + $Variant)",
                    row.Script + " does not build its TestId the way the manifest expects.");
                foreach (ManifestVariant variant in variants)
                {
                    Assert.AreEqual(row.TestId + "-v" + variant.Variant, variant.TestId);
                }

                continue;
            }

            StringAssert.Contains(text, "-TestId '" + row.TestId + "'", row.Script + " does not declare -TestId '" + row.TestId + "'.");
        }
    }

    [TestMethod]
    public void MarkersAgreeWithExpectations()
    {
        JsonElement expectations = ReadExpectationsThroughARealPowerShell51Child();
        IReadOnlyList<ManifestRow> rows = LoadManifest();

        foreach (ManifestRow row in rows)
        {
            if (row.Halves != 2)
            {
                continue;
            }

            // Test 10 is keyed by its first variant in expectations.psd1, the same convention the
            // self-test itself uses: 10 as variant 1.
            string testId = row.Variants is { Count: > 0 } variants ? variants[0].TestId : row.TestId;

            HashSet<string> firstHalfCriteria = CriteriaIds(expectations, testId + "|first");
            HashSet<string> secondHalfCriteria = CriteriaIds(expectations, testId + "|resume");

            foreach (string id in row.FirstHalfOnlyCriteriaIds)
            {
                Assert.IsTrue(firstHalfCriteria.Contains(id), row.TestId + "'s first-half marker '" + id + "' is not in expectations.psd1's first half.");
                Assert.IsFalse(secondHalfCriteria.Contains(id), row.TestId + "'s first-half marker '" + id + "' also appears in the second half, so it does not tell the halves apart.");
            }

            foreach (string id in row.SecondHalfOnlyCriteriaIds)
            {
                Assert.IsTrue(secondHalfCriteria.Contains(id), row.TestId + "'s second-half marker '" + id + "' is not in expectations.psd1's second half.");
                Assert.IsFalse(firstHalfCriteria.Contains(id), row.TestId + "'s second-half marker '" + id + "' also appears in the first half, so it does not tell the halves apart.");
            }

            if (row.FirstHalfOnlyFindingNames.Count > 0)
            {
                Assert.AreEqual(0, firstHalfCriteria.Count,
                    row.TestId + "'s first half is marked by a finding only, but expectations.psd1 records criteria for it too.");
            }
        }
    }

    private static HashSet<string> CriteriaIds(JsonElement expectations, string key)
    {
        Assert.IsTrue(expectations.TryGetProperty(key, out JsonElement half), "expectations.psd1 has no entry '" + key + "'.");
        Assert.IsTrue(half.TryGetProperty("none", out JsonElement noneCase), "'" + key + "' has no 'none' case.");
        Assert.IsTrue(noneCase.TryGetProperty("Criteria", out JsonElement criteria), "'" + key + "'.none has no Criteria.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in criteria.EnumerateObject())
        {
            ids.Add(property.Name);
        }

        return ids;
    }

    // Reads selftest\expectations.psd1 the same way LiveTest.psm1's own scripts would: a real
    // Windows PowerShell 5.1 child, started through the test project's WindowsPowerShellHost so
    // it never inherits a PowerShell 7 parent's PSModulePath, running Import-PowerShellDataFile.
    private static JsonElement ReadExpectationsThroughARealPowerShell51Child()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so expectations.psd1 was not read.");
        }

        string expectationsPath = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "selftest", "expectations.psd1");
        Assert.IsTrue(File.Exists(expectationsPath), "expectations.psd1 was not found at " + expectationsPath + ".");

        string scriptFolder = Path.Combine(Path.GetTempPath(), "earshot-manifest-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scriptFolder);
        string scriptPath = Path.Combine(scriptFolder, "read-expectations.ps1");
        try
        {
            File.WriteAllText(scriptPath,
                "param([string]$Data)" + Environment.NewLine +
                "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
                "$expectations = Import-PowerShellDataFile -LiteralPath $Data" + Environment.NewLine +
                "($expectations | ConvertTo-Json -Depth 12 -Compress)" + Environment.NewLine);

            ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Data", expectationsPath,
            });

            using var process = new Process { StartInfo = info };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(RunTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Reading expectations.psd1 through Windows PowerShell 5.1 did not finish within " + RunTimeout + ".");
            }

            string stdout = output.GetAwaiter().GetResult();
            string stderr = errors.GetAwaiter().GetResult();
            Assert.AreEqual(0, process.ExitCode, "Reading expectations.psd1 failed: " + stderr);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stdout), "Reading expectations.psd1 produced no output. " + stderr);

            using JsonDocument document = JsonDocument.Parse(stdout);
            return document.RootElement.Clone();
        }
        finally
        {
            Directory.Delete(scriptFolder, recursive: true);
        }
    }
}
