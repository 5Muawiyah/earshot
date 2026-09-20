using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Fix from the coordinator's reading of the S5 screenshot: a row must show a plain name and one
// line saying what the test proves, not the TestId alone, and that text must be drawn from the
// script itself, never invented. This reads each script's own -Title and -Settles arguments to
// New-LiveTestRun with the real PowerShell parser and checks the manifest against them exactly.
[TestClass]
public sealed class ManifestTitleSettlesTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(3);

    private static IReadOnlyList<ManifestRow> LoadManifest() =>
        Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));

    [TestMethod]
    public void EveryRowsTitleAndSettlesMatchTheScriptsOwnLiteralsExactly()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        JsonElement found = RunExtractor(host, liveTestsRoot);
        var byScript = new Dictionary<string, (string? Title, string? Settles, bool TitleIsLiteral, bool SettlesIsLiteral)>(StringComparer.Ordinal);
        foreach (JsonElement file in found.EnumerateArray())
        {
            byScript[file.GetProperty("file").GetString()!] = (
                file.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                file.TryGetProperty("settles", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                file.GetProperty("titleIsLiteral").GetBoolean(),
                file.GetProperty("settlesIsLiteral").GetBoolean());
        }

        var problems = new List<string>();
        foreach (ManifestRow row in LoadManifest())
        {
            if (!byScript.TryGetValue(row.Script, out var script))
            {
                problems.Add(row.Script + ": no New-LiveTestRun call was found at all.");
                continue;
            }

            // Test 10 builds its Title at run time (variant number); the manifest instead carries
            // one literal per variant, checked separately below.
            if (row.Number != "10")
            {
                if (!script.TitleIsLiteral)
                {
                    problems.Add(row.Script + ": -Title is not a plain literal, so it cannot be checked.");
                }
                else if (script.Title != row.Title)
                {
                    problems.Add(row.Script + ": manifest title '" + row.Title + "' does not match the script's own '" + script.Title + "'.");
                }
            }

            if (!script.SettlesIsLiteral)
            {
                problems.Add(row.Script + ": -Settles is not a plain literal, so it cannot be checked.");
            }
            else if (script.Settles != row.Settles)
            {
                problems.Add(row.Script + ": manifest settles '" + row.Settles + "' does not match the script's own '" + script.Settles + "'.");
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void Test10sVariantTitlesMatchTheLiteralPrefixTheScriptBuildsFrom()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        string scriptText = File.ReadAllText(Path.Combine(liveTestsRoot, "10-ShutdownMessages.ps1"));
        StringAssert.Contains(scriptText, "-Title ('End-session messages, variant ' + $Variant)");

        ManifestRow row10 = LoadManifest().Single(r => r.Number == "10");
        Assert.IsNotNull(row10.Variants);
        foreach (ManifestVariant variant in row10.Variants!)
        {
            Assert.AreEqual("End-session messages, variant " + variant.Variant, variant.Title);
        }
    }

    private static JsonElement RunExtractor(string host, string root)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "earshot-title-settles-extract-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, ExtractorScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Root", root,
            });

            var output = new StringBuilder();
            var errors = new StringBuilder();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(RunTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The title/settles extractor did not finish within " + RunTimeout + ".");
            }

            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, errors.ToString());
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    // Reads every 16-shipped script with the real PowerShell parser and reports the literal (or
    // not) -Title and -Settles arguments passed to New-LiveTestRun.
    private const string ExtractorScript = """
        [CmdletBinding()]
        param([Parameter(Mandatory = $true)][string]$Root)

        $ErrorActionPreference = 'Stop'
        $files = @(Get-ChildItem -LiteralPath $Root -Filter '*.ps1' -File | Where-Object { $_.Name -match '^\d{2}-' } | Sort-Object Name)
        $report = @()

        foreach ($file in $files)
        {
            $errors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
            $row = [ordered]@{ file = $file.Name; title = $null; titleIsLiteral = $false; settles = $null; settlesIsLiteral = $false }

            foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))
            {
                if ($c.GetCommandName() -ne 'New-LiveTestRun') { continue }

                $elements = $c.CommandElements
                for ($i = 1; $i -lt $elements.Count; $i++)
                {
                    $e = $elements[$i]
                    if (-not ($e -is [System.Management.Automation.Language.CommandParameterAst])) { continue }
                    if ($e.ParameterName -ne 'Title' -and $e.ParameterName -ne 'Settles') { continue }

                    $argExpr = $e.Argument
                    if ($null -eq $argExpr -and ($i + 1) -lt $elements.Count -and
                        -not ($elements[$i + 1] -is [System.Management.Automation.Language.CommandParameterAst]))
                    {
                        $argExpr = $elements[$i + 1]
                    }

                    $isLiteral = ($null -ne $argExpr) -and ($argExpr -is [System.Management.Automation.Language.StringConstantExpressionAst])
                    $value = $null
                    if ($isLiteral) { $value = $argExpr.Value }

                    if ($e.ParameterName -eq 'Title') { $row.title = $value; $row.titleIsLiteral = $isLiteral }
                    else { $row.settles = $value; $row.settlesIsLiteral = $isLiteral }
                }

                break
            }

            $report += $row
        }

        ($report | ConvertTo-Json -Depth 6 -Compress)
        """;
}
