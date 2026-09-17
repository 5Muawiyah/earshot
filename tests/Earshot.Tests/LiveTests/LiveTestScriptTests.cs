using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The owner's live device tests are PowerShell scripts under tools\live-tests. They are never run
// here: running one would send a real connect request, disable a real device node or start an
// elevated task. These tests only read them.
//
// Two things are checked. First, every script parses, using the PowerShell parser itself rather
// than a guess at its grammar: System.Management.Automation is not referenced by this project, so
// the parse runs in powershell.exe and reports back as JSON. Second, every Earshot command line a
// script passes is fed to the application's own argument parsers, so a script can never call a
// probe or diag target with arguments the application would reject.
//
// The same pass records, for each call, whether it was marked as a live step and whether it said
// what it does, which is how the "ask before anything changes" rule is held to.
[TestClass]
public sealed class LiveTestScriptTests
{
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    // The scripts that must exist. A test that is deleted or renamed without the launcher and this
    // list being updated fails here.
    private static readonly string[] ExpectedScripts =
    [
        "00-Restore.ps1",
        "01-A2dpOneShot.ps1",
        "02-Disconnect.ps1",
        "03-AllowPages.ps1",
        "04-BlockAndReboot.ps1",
        "05-Allow.ps1",
        "06-Handsfree.ps1",
        "07-TaskRunEx.ps1",
        "08-AcceptancePowerCycle.ps1",
        "09-ShutdownWhileConnected.ps1",
        "10-ShutdownMessages.ps1",
        "11-BatteryDisconnected.ps1",
        "12-CallbackThread.ps1",
        "13-GraceWindow.ps1",
        "14-SetDeviceRefusal.ps1",
        "15-UninstallReversal.ps1",
        "LiveTest.psm1",
        "Run-LiveTests.ps1",
    ];

    // A script builds some arguments from a variable rather than a literal, because the same helper
    // sends several of them. Each variable stands for a value of one shape, and the stand-in here is
    // a well formed example of that shape. A variable that is not in this table fails the test, so a
    // new one cannot slip in unchecked.
    private static readonly Dictionary<string, string> StandIns = new(StringComparer.Ordinal)
    {
        ["Action"] = "reconnect",                                          // reconnect or disconnect
        ["Filter"] = "src",                                                // src, wave or all
        ["Address"] = "5A6B7C8D9EAF",                                      // 12 upper-case hex
        ["Container"] = "1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d",            // a device container
        ["Sid"] = "S-1-5-21-1111111111-2222222222-3333333333-1001",        // a user SID
        ["evidence"] = @"C:\evidence\report.json",                         // a file to write to
        ["iconFolder"] = @"C:\evidence\icons",                             // a folder to write into
    };

    private static ScriptSet? _scripts;

    [ClassInitialize]
    public static void ReadTheScripts(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _scripts = Analyse();
    }

    private static ScriptSet Scripts => _scripts ?? throw new AssertFailedException("The scripts were not read.");

    [TestMethod]
    public void EveryScriptParses()
    {
        var problems = new List<string>();
        foreach (ScriptFile file in Scripts.Files)
        {
            foreach (string error in file.Errors)
            {
                problems.Add(file.Name + ": " + error);
            }
        }

        Assert.IsEmpty(problems, "The PowerShell parser rejected:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.IsGreaterThan(0, Scripts.Files.Count, "No script was found to parse.");
    }

    [TestMethod]
    public void EveryExpectedScriptIsThere()
    {
        var names = Scripts.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string expected in ExpectedScripts)
        {
            Assert.Contains(expected, names, expected + " is missing from tools\\live-tests.");
        }
    }

    // The launcher lists the tests for the owner. A script it does not name cannot be found.
    [TestMethod]
    public void EveryTestScriptIsNamedInTheLauncher()
    {
        ScriptFile launcher = Scripts.Files.Single(f => string.Equals(f.Name, "Run-LiveTests.ps1", StringComparison.OrdinalIgnoreCase));
        var listed = launcher.Strings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ExpectedScripts)
        {
            if (name.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Run-LiveTests.ps1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Contains(name, listed, name + " is not named in Run-LiveTests.ps1, so the owner cannot start it from the list.");
        }
    }

    [TestMethod]
    public void EveryProbeCommandIsAcceptedByTheApplicationsOwnParser()
    {
        int checkedCommands = 0;
        foreach (CommandCall call in Scripts.Calls)
        {
            string[] args = Fill(call);
            if (args.Length == 0 || !string.Equals(args[0], "probe", StringComparison.Ordinal))
            {
                continue;
            }

            checkedCommands++;
            bool accepted = Program.TryParseProbeArgs(args, out Program.ProbeRequest? request, out string? error);
            Assert.IsTrue(accepted, Where(call) + " passes a probe command the application rejects: " + error);
            Assert.IsNotNull(request);
        }

        Assert.IsGreaterThan(0, checkedCommands, "No probe command was found in the scripts, which cannot be right.");
    }

    [TestMethod]
    public void EveryDiagCommandIsAcceptedByTheApplicationsOwnParser()
    {
        int checkedCommands = 0;
        foreach (CommandCall call in Scripts.Calls)
        {
            string[] args = Fill(call);
            if (args.Length == 0 || !string.Equals(args[0], "diag", StringComparison.Ordinal))
            {
                continue;
            }

            checkedCommands++;
            bool accepted = Program.TryParseDiagArgs(args, out Program.DiagRequest? request, out string? error);
            Assert.IsTrue(accepted, Where(call) + " passes a diag command the application rejects: " + error);
            Assert.IsNotNull(request);
        }

        Assert.IsGreaterThan(0, checkedCommands, "No diag command was found in the scripts, which cannot be right.");
    }

    // The scripts send diag ks with the action and the filter in variables, so the stand-in above
    // covers only one pair. Every pair a script can pass is checked here instead.
    [TestMethod]
    [DataRow("reconnect", "src")]
    [DataRow("reconnect", "wave")]
    [DataRow("reconnect", "all")]
    [DataRow("disconnect", "src")]
    [DataRow("disconnect", "wave")]
    [DataRow("disconnect", "all")]
    public void EveryActionAndFilterPairTheScriptsCanPassIsAccepted(string action, string filter)
    {
        string[] args = ["diag", "ks", action, filter];
        Assert.IsTrue(Program.TryParseDiagArgs(args, out _, out string? error), error);
    }

    [TestMethod]
    public void EveryInstallAndUninstallCommandIsAcceptedByTheApplicationsOwnParser()
    {
        int checkedCommands = 0;
        foreach (CommandCall call in Scripts.Calls)
        {
            string[] args = Fill(call);
            if (args.Length == 0)
            {
                continue;
            }

            if (string.Equals(args[0], "install", StringComparison.Ordinal))
            {
                checkedCommands++;
                Assert.IsTrue(
                    Program.TryParseInstallArgs(args, out _, out string? problem),
                    Where(call) + " passes an install command the application rejects: " + problem);
            }
            else if (string.Equals(args[0], "uninstall", StringComparison.Ordinal))
            {
                checkedCommands++;
                Assert.HasCount(1, args, Where(call) + ": uninstall takes no arguments, and the application rejects extra ones.");
            }
        }

        Assert.IsGreaterThan(0, checkedCommands, "No install or uninstall command was found in the scripts.");
    }

    // Every step that changes a device, a service, a task or a folder has to say what it does and
    // wait for the owner. Invoke-Earshot does that when it is given -Live, and Invoke-EarshotElevated
    // always does. A diag command is by definition such a step.
    [TestMethod]
    public void EveryDiagStepAsksTheOwnerFirstAndSaysWhatItDoes()
    {
        foreach (CommandCall call in Scripts.Calls)
        {
            string[] args = Fill(call);
            bool changesSomething = args.Length > 0 && args[0] is "diag" or "install" or "uninstall";
            if (!changesSomething)
            {
                continue;
            }

            if (string.Equals(call.Function, "Invoke-Earshot", StringComparison.Ordinal))
            {
                Assert.Contains("Live", call.Parameters, Where(call) + " runs " + args[0] + " without marking it a live step, so it would not ask first.");
            }

            Assert.Contains("Consequence", call.Parameters, Where(call) + " does not say what it does before it asks.");
        }
    }

    // A probe changes nothing, so it is never announced as a live step. Announcing one would train
    // the owner to type y without reading.
    [TestMethod]
    public void NoReadOnlyProbeIsAnnouncedAsALiveStep()
    {
        foreach (CommandCall call in Scripts.Calls)
        {
            string[] args = Fill(call);
            if (args.Length == 0 || !string.Equals(args[0], "probe", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("Live", call.Parameters, Where(call) + " marks a read-only probe as a live step.");
            Assert.AreEqual("Invoke-Earshot", call.Function, Where(call) + " runs a probe through the elevated helper, which it never needs.");
        }
    }

    [TestMethod]
    public void EveryArgumentIsEitherALiteralOrAKnownStandIn()
    {
        foreach (CommandCall call in Scripts.Calls)
        {
            foreach (CommandElement element in call.Elements)
            {
                Assert.AreNotEqual(
                    "expression",
                    element.Kind,
                    Where(call) + " builds an argument from the expression " + element.Value +
                    ". Put it in a variable first, and add that variable to the stand-in table, so the grammar can be checked.");

                if (string.Equals(element.Kind, "variable", StringComparison.Ordinal))
                {
                    Assert.Contains(
                        element.Value,
                        StandIns.Keys,
                        Where(call) + " uses the variable " + element.Value + ", which has no stand-in value, so its command line cannot be checked.");
                }
            }
        }
    }

    // The restore script is what the owner runs when a test stops in the middle, so the README has
    // to explain it before it explains anything else.
    [TestMethod]
    public void TheReadmeDocumentsTheRestoreScriptFirst()
    {
        string readme = File.ReadAllText(Path.Combine(ScriptFolder(), "README.md"));
        string[] headings = readme.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .ToArray();

        Assert.IsGreaterThan(0, headings.Length, "The README has no sections.");
        Assert.Contains("put the machine back", headings[0], StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(readme, "00-Restore.ps1");
    }

    [TestMethod]
    public void TheScriptsCarryNoEmDash()
    {
        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(ScriptFolder(), "*", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains('\u2014', StringComparison.Ordinal))
                {
                    found.Add(Path.GetFileName(path) + ":" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        Assert.IsEmpty(found, "Em-dashes are not used anywhere: " + string.Join(", ", found));
    }

    // ------------------------------------------------------------------ reading the scripts

    private static string Where(CommandCall call) =>
        call.File + " line " + call.Line.ToString(CultureInfo.InvariantCulture);

    // The command line a call passes, with each variable replaced by its stand-in. A variable with no
    // stand-in becomes a value nothing accepts, so the grammar test fails and names it.
    private static string[] Fill(CommandCall call)
    {
        var args = new string[call.Elements.Count];
        for (int i = 0; i < call.Elements.Count; i++)
        {
            CommandElement element = call.Elements[i];
            args[i] = element.Kind switch
            {
                "literal" => element.Value,
                "variable" => StandIns.TryGetValue(element.Value, out string? value) ? value : "<no stand-in for " + element.Value + ">",
                _ => "<" + element.Kind + ">",
            };
        }

        return args;
    }

    private static ScriptSet Analyse()
    {
        string folder = ScriptFolder();
        string analyser = Path.Combine(Path.GetTempPath(), "earshot-live-test-analysis-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(analyser, AnalyserScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            (int exit, string output, string errors) = RunPowerShell(analyser, folder);
            if (exit != 0)
            {
                Assert.Fail("The script analysis failed with exit " + exit.ToString(CultureInfo.InvariantCulture) +
                    Environment.NewLine + errors + Environment.NewLine + output);
            }

            return Read(output);
        }
        finally
        {
            File.Delete(analyser);
        }
    }

    private static (int Exit, string Output, string Errors) RunPowerShell(string scriptPath, string root)
    {
        var info = new ProcessStartInfo(PowerShellHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Root", root })
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
        if (!process.WaitForExit(ParseTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The script analysis did not finish within " + ParseTimeout + ".");
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

    private static string PowerShellHost()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string host = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(host) ? host : "powershell.exe";
    }

    private static ScriptSet Read(string json)
    {
        var files = new List<ScriptFile>();
        var calls = new List<CommandCall>();
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement file in Items(document.RootElement, "files"))
        {
            string name = file.GetProperty("name").GetString() ?? "";
            files.Add(new ScriptFile(
                name,
                ReadStrings(file, "errors"),
                ReadStrings(file, "strings")));

            foreach (JsonElement call in Items(file, "calls"))
            {
                var elements = new List<CommandElement>();
                foreach (JsonElement element in Items(call, "elements"))
                {
                    elements.Add(new CommandElement(
                        element.GetProperty("kind").GetString() ?? "",
                        element.GetProperty("value").GetString() ?? ""));
                }

                calls.Add(new CommandCall(
                    name,
                    call.GetProperty("line").GetInt32(),
                    call.GetProperty("function").GetString() ?? "",
                    ReadStrings(call, "parameters"),
                    elements));
            }
        }

        return new ScriptSet(files, calls);
    }

    // Some PowerShell versions write a list of one as that one value rather than as a list, so a
    // single object and a missing member are both read as well as a list.
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
            yield break;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            yield return item;
        }
    }

    private static List<string> ReadStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return [value.GetString() ?? ""];
        }

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item.GetString() ?? "");
        }

        return items;
    }

    private static string ScriptFolder() => Path.Combine(RepositoryRoot(), "tools", "live-tests");

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

    private sealed record ScriptSet(IReadOnlyList<ScriptFile> Files, IReadOnlyList<CommandCall> Calls);

    private sealed record ScriptFile(string Name, IReadOnlyList<string> Errors, IReadOnlyList<string> Strings);

    private sealed record CommandCall(
        string File,
        int Line,
        string Function,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<CommandElement> Elements);

    private sealed record CommandElement(string Kind, string Value);

    // Parses each script with the PowerShell parser and reports what it found as JSON. ParseFile
    // reads a file and builds a syntax tree; it never runs anything in it, so no live step can
    // happen here. The command lines come from the tree, not from a text search, so a call that is
    // split over several lines is still read whole.
    private const string AnalyserScript = """
        [CmdletBinding()]
        param([Parameter(Mandatory = $true)][string]$Root)

        $ErrorActionPreference = 'Stop'
        $files = @()
        $scripts = @(Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object { $_.Extension -eq '.ps1' -or $_.Extension -eq '.psm1' } |
            Sort-Object FullName)

        foreach ($file in $scripts)
        {
            $errors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)

            $messages = @()
            foreach ($e in $errors)
            {
                $messages += ('line ' + $e.Extent.StartLineNumber + ', column ' + $e.Extent.StartColumnNumber + ': ' + $e.Message)
            }

            $calls = @()
            $strings = @()
            if ($null -ne $ast)
            {
                foreach ($s in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true))
                {
                    $strings += $s.Value
                }

                foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))
                {
                    $name = $c.GetCommandName()
                    if ($name -ne 'Invoke-Earshot' -and $name -ne 'Invoke-EarshotElevated') { continue }

                    $parameters = @()
                    $commandArgument = $null
                    $elements = $c.CommandElements
                    for ($i = 1; $i -lt $elements.Count; $i++)
                    {
                        $element = $elements[$i]
                        if ($element -is [System.Management.Automation.Language.CommandParameterAst])
                        {
                            $parameters += $element.ParameterName
                            if ($element.ParameterName -eq 'Command')
                            {
                                if ($null -ne $element.Argument) { $commandArgument = $element.Argument }
                                elseif ($i + 1 -lt $elements.Count) { $commandArgument = $elements[$i + 1] }
                            }
                        }
                    }

                    $parts = @()
                    if ($null -ne $commandArgument)
                    {
                        # @('a', 'b') is an array expression holding an array literal; @('a') holds one
                        # expression and no literal. Both are unpacked to the same list of elements.
                        $items = @()
                        if ($commandArgument -is [System.Management.Automation.Language.ArrayLiteralAst])
                        {
                            $items = $commandArgument.Elements
                        }
                        elseif ($commandArgument -is [System.Management.Automation.Language.ArrayExpressionAst])
                        {
                            foreach ($statement in $commandArgument.SubExpression.Statements)
                            {
                                if (-not ($statement -is [System.Management.Automation.Language.PipelineAst])) { continue }
                                foreach ($piece in $statement.PipelineElements)
                                {
                                    if (-not ($piece -is [System.Management.Automation.Language.CommandExpressionAst])) { continue }
                                    if ($piece.Expression -is [System.Management.Automation.Language.ArrayLiteralAst])
                                    {
                                        $items += $piece.Expression.Elements
                                    }
                                    else
                                    {
                                        $items += $piece.Expression
                                    }
                                }
                            }
                        }
                        else
                        {
                            $items = @($commandArgument)
                        }

                        foreach ($item in $items)
                        {
                            if ($item -is [System.Management.Automation.Language.StringConstantExpressionAst])
                            {
                                $parts += [ordered]@{ kind = 'literal'; value = $item.Value }
                            }
                            elseif ($item -is [System.Management.Automation.Language.VariableExpressionAst])
                            {
                                $parts += [ordered]@{ kind = 'variable'; value = $item.VariablePath.UserPath }
                            }
                            else
                            {
                                $parts += [ordered]@{ kind = 'expression'; value = $item.Extent.Text }
                            }
                        }
                    }

                    $calls += [ordered]@{
                        function = $name
                        line = $c.Extent.StartLineNumber
                        parameters = $parameters
                        elements = $parts
                    }
                }
            }

            $files += [ordered]@{
                name = $file.Name
                path = $file.FullName
                errors = $messages
                calls = $calls
                strings = $strings
            }
        }

        ([ordered]@{ root = $Root; files = $files } | ConvertTo-Json -Depth 8)
        """;
}
