using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Every wording entry's scriptText appears in the
// script it is filed under, and every literal Read-Answer/Read-Note/Wait-Owner
// question-or-instruction, every literal -Consequence a live step passes, and every literal
// element of Show-Preconditions' own -Preconditions and -PhysicalActions arrays has an entry. Read
// with the real PowerShell parser, not a regex, the same way LiveTestScriptTests reads the
// scripts, so a call split over several lines or reordered parameters is still found.
[TestClass]
public sealed class WordingManifestTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(3);

    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    [TestMethod]
    public void EveryWordingEntrysScriptTextIsInItsScript()
    {
        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));
        Dictionary<string, string> scriptByNumber = rows.ToDictionary(r => r.Number, r => r.Script, StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (WordingEntry entry in LoadWording())
        {
            if (!scriptByNumber.TryGetValue(entry.Test, out string? script))
            {
                problems.Add("wording.json names test '" + entry.Test + "', which is not a manifest row.");
                continue;
            }

            string text = File.ReadAllText(Path.Combine(liveTestsRoot, script));
            if (!text.Contains(entry.ScriptText, StringComparison.Ordinal))
            {
                problems.Add(script + " does not contain the literal wording.json says it does (" + entry.Kind + "): " + entry.ScriptText);
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void EveryPromptLiteralInEveryScriptHasAWordingEntry()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string liveTestsRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests");
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "tests.json"));
        Dictionary<string, string> numberByScript = rows.ToDictionary(r => r.Script, r => r.Number, StringComparer.Ordinal);
        IReadOnlyList<WordingEntry> wording = LoadWording();

        JsonElement found = RunExtractor(host, liveTestsRoot);
        var missing = new List<string>();
        var nonLiteral = new List<string>();

        foreach (JsonElement file in found.EnumerateArray())
        {
            string name = file.GetProperty("file").GetString()!;
            if (!numberByScript.TryGetValue(name, out string? number))
            {
                continue;
            }

            foreach (JsonElement call in file.GetProperty("calls").EnumerateArray())
            {
                string kindText = call.GetProperty("kind").GetString()!;
                bool isLiteral = call.GetProperty("isLiteral").GetBoolean();
                int line = call.GetProperty("line").GetInt32();

                if (!isLiteral)
                {
                    nonLiteral.Add(name + " line " + line + ": a " + kindText + " is not a plain literal, so it cannot be checked against wording.json.");
                    continue;
                }

                string value = call.GetProperty("value").GetString()!;
                WordingKind kind = kindText switch
                {
                    "question" => WordingKind.Question,
                    "note" => WordingKind.Note,
                    "instruction" => WordingKind.Instruction,
                    "precondition" => WordingKind.Precondition,
                    "action" => WordingKind.Action,
                    _ => WordingKind.Consequence,
                };

                if (Wording.Find(wording, number, kind, value) is null)
                {
                    missing.Add(name + " line " + line + " (" + kindText + "): " + value);
                }
            }
        }

        Assert.IsEmpty(nonLiteral, string.Join(Environment.NewLine, nonLiteral));
        Assert.IsEmpty(missing, "No wording.json entry for:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static JsonElement RunExtractor(string host, string root)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "earshot-wording-extract-" + Guid.NewGuid().ToString("N") + ".ps1");
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
                Assert.Fail("The wording extractor did not finish within " + RunTimeout + ".");
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

    // Reads every 16-shipped script with the real PowerShell parser and reports, for each call to
    // Read-Answer, Read-Note, Wait-Owner or a live Invoke-Earshot/Invoke-EarshotElevated, the
    // literal it passed for Question, Text or Consequence.
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
            $calls = @()

            foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))
            {
                $name = $c.GetCommandName()

                # Show-Preconditions takes two string arrays (-Preconditions, -PhysicalActions)
                # rather than one Question/Text/Consequence literal, so it is walked separately from
                # the single-literal callers below. Each array element becomes its own reported call
                # (kind precondition or action). An element that is a plain string literal is
                # reported whole; an element that is a literal prefix concatenated with a dynamic,
                # already-plain suffix (10-ShutdownMessages.ps1's own per-variant restart
                # instruction: 'Restart the machine this way: ' + $variants[$Variant]) reports only
                # its literal prefix, since that is the only part wording.json can ever pin an exact
                # sentence to; anything else (a bare variable, a call, a concatenation with no
                # leading literal) is reported non-literal, the same as every other caller here, so
                # it fails loudly rather than silently passing nothing through.
                if ($name -eq 'Show-Preconditions')
                {
                    $elements = $c.CommandElements
                    for ($i = 1; $i -lt $elements.Count; $i++)
                    {
                        $e = $elements[$i]
                        if (-not ($e -is [System.Management.Automation.Language.CommandParameterAst])) { continue }
                        if ($e.ParameterName -ne 'Preconditions' -and $e.ParameterName -ne 'PhysicalActions') { continue }

                        $argExpr = $e.Argument
                        if ($null -eq $argExpr -and ($i + 1) -lt $elements.Count -and
                            -not ($elements[$i + 1] -is [System.Management.Automation.Language.CommandParameterAst]))
                        {
                            $argExpr = $elements[$i + 1]
                        }

                        $arrayKind = if ($e.ParameterName -eq 'Preconditions') { 'precondition' } else { 'action' }

                        $arrayElements = @()
                        if ($argExpr -is [System.Management.Automation.Language.ArrayExpressionAst])
                        {
                            $inner = $argExpr.FindAll({ $args[0] -is [System.Management.Automation.Language.ArrayLiteralAst] }, $false) | Select-Object -First 1
                            if ($null -ne $inner) { $arrayElements = $inner.Elements }
                            else { $arrayElements = $argExpr.FindAll({ $args[0] -is [System.Management.Automation.Language.StringConstantExpressionAst] -or $args[0] -is [System.Management.Automation.Language.BinaryExpressionAst] }, $true) }
                        }
                        elseif ($argExpr -is [System.Management.Automation.Language.ArrayLiteralAst])
                        {
                            $arrayElements = $argExpr.Elements
                        }
                        elseif ($null -ne $argExpr)
                        {
                            $arrayElements = @($argExpr)
                        }

                        foreach ($rawEl in $arrayElements)
                        {
                            # An element written in parentheses, ('literal ' + $x), parses as a
                            # ParenExpressionAst wrapping the real BinaryExpressionAst rather than
                            # the binary expression itself; unwrap it before classifying.
                            $el = $rawEl
                            while ($el -is [System.Management.Automation.Language.ParenExpressionAst])
                            {
                                $el = $el.Pipeline.PipelineElements[0].Expression
                            }

                            if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst])
                            {
                                $calls += [ordered]@{ line = $c.Extent.StartLineNumber; kind = $arrayKind; isLiteral = $true; value = $el.Value }
                            }
                            elseif ($el -is [System.Management.Automation.Language.BinaryExpressionAst] -and
                                    $el.Left -is [System.Management.Automation.Language.StringConstantExpressionAst])
                            {
                                $calls += [ordered]@{ line = $c.Extent.StartLineNumber; kind = $arrayKind; isLiteral = $true; value = $el.Left.Value }
                            }
                            else
                            {
                                $calls += [ordered]@{ line = $c.Extent.StartLineNumber; kind = $arrayKind; isLiteral = $false; value = $null }
                            }
                        }
                    }

                    continue
                }

                if ($name -ne 'Read-Answer' -and $name -ne 'Read-Note' -and $name -ne 'Wait-Owner' -and
                    $name -ne 'Invoke-Earshot' -and $name -ne 'Invoke-EarshotElevated' -and $name -ne 'Invoke-KsStep') { continue }

                # A local wrapper (01 and 02's Invoke-KsStep) that itself calls
                # Invoke-Earshot -Live -Consequence $Consequence, forwarding its own parameter:
                # that forwarding call is not a fresh literal to check, so a Question/Text/
                # Consequence argument that is a bare read of one of the ENCLOSING function's own
                # parameters is skipped rather than reported. The wrapper's own call sites
                # (Invoke-KsStep itself) are tracked instead, as though it were Invoke-Earshot
                # -Live.
                $enclosingParams = @{}
                $walk = $c.Parent
                while ($null -ne $walk)
                {
                    if ($walk -is [System.Management.Automation.Language.FunctionDefinitionAst])
                    {
                        # A function's parameters are on .Parameters for "function Foo($a) {}"
                        # and on .Body.ParamBlock.Parameters for a param() block in the body,
                        # which is the style every script here uses.
                        $declared = $walk.Parameters
                        if (($null -eq $declared -or $declared.Count -eq 0) -and $null -ne $walk.Body.ParamBlock)
                        {
                            $declared = $walk.Body.ParamBlock.Parameters
                        }

                        foreach ($p in @($declared)) { $enclosingParams[$p.Name.VariablePath.UserPath] = $true }
                        break
                    }

                    $walk = $walk.Parent
                }

                $isLive = ($name -eq 'Invoke-EarshotElevated' -or $name -eq 'Invoke-KsStep')
                $elements = $c.CommandElements
                $wanted = @()
                for ($i = 1; $i -lt $elements.Count; $i++)
                {
                    $e = $elements[$i]
                    if (-not ($e -is [System.Management.Automation.Language.CommandParameterAst])) { continue }
                    if ($e.ParameterName -eq 'Live') { $isLive = $true; continue }
                    if ($e.ParameterName -ne 'Question' -and $e.ParameterName -ne 'Text' -and $e.ParameterName -ne 'Consequence') { continue }

                    $argExpr = $e.Argument
                    if ($null -eq $argExpr -and ($i + 1) -lt $elements.Count -and
                        -not ($elements[$i + 1] -is [System.Management.Automation.Language.CommandParameterAst]))
                    {
                        $argExpr = $elements[$i + 1]
                    }

                    if ($argExpr -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $enclosingParams.ContainsKey($argExpr.VariablePath.UserPath))
                    {
                        continue
                    }

                    $isLiteral = ($null -ne $argExpr) -and ($argExpr -is [System.Management.Automation.Language.StringConstantExpressionAst])
                    $value = $null
                    if ($isLiteral) { $value = $argExpr.Value }
                    $wanted += [ordered]@{ parameter = $e.ParameterName; isLiteral = $isLiteral; value = $value }
                }

                foreach ($item in $wanted)
                {
                    if ($item.parameter -eq 'Consequence' -and $name -eq 'Invoke-Earshot' -and -not $isLive) { continue }

                    $kind = 'consequence'
                    if ($name -eq 'Read-Answer') { $kind = 'question' }
                    elseif ($name -eq 'Read-Note') { $kind = 'note' }
                    elseif ($name -eq 'Wait-Owner') { $kind = 'instruction' }

                    $calls += [ordered]@{
                        line      = $c.Extent.StartLineNumber
                        kind      = $kind
                        isLiteral = $item.isLiteral
                        value     = $item.value
                    }
                }
            }

            $report += [ordered]@{ file = $file.Name; calls = $calls }
        }

        ($report | ConvertTo-Json -Depth 6 -Compress)
        """;
}
