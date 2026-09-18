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
        // Made up, and only their shape matters: the parsers under test check 12 upper-case hex and a
        // GUID. These are the same synthetic values the rest of the tests use, so no real device
        // identifier is committed.
        ["Address"] = "0A1B2C3D4E8C",                                      // 12 upper-case hex
        ["Container"] = "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13",            // a device container
        ["Sid"] = "S-1-5-21-1111111111-2222222222-3333333333-1001",        // a user SID
        ["evidence"] = @"C:\evidence\report.json",                         // a file to write to
        ["iconFolder"] = @"C:\evidence\icons",                             // a folder to write into
    };

    // Commands whose result is always there, so a method may be called straight on it. Anything else
    // goes through ('' + (...)) or [string](...), because a helper returns $null for a report member
    // that is missing and a live test must record that, not throw on it.
    private static readonly HashSet<string> CommandsThatNeverReturnNull = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get-Date",
    };

    // Read once, however the test classes are ordered: LiveTestFieldTests reads the field names from
    // the same pass.
    private static readonly Lazy<ScriptSet> Analysis = new(Analyse);

    private static ScriptSet Scripts => Analysis.Value;

    // Every name the scripts read out of a JSON report, with where it is read. LiveTestFieldTests
    // checks each one against the writer that is supposed to produce it.
    internal static IReadOnlyList<FieldRead> FieldReads => Scripts.FieldReads;

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

    // Parsing is not enough: a script that parses can still throw on its first criterion. PowerShell
    // converts the right operand of '+' to the type of the left one, so ($report.Disabled + ' of ')
    // throws "Cannot convert value" when Disabled is a number, which is what every count read out of
    // a JSON report is. The script's own try/catch turns that into "run: fail" with nothing recorded,
    // so the owner loses the sitting. Every text join must therefore start with a string.
    [TestMethod]
    public void EveryTextJoinStartsWithText()
    {
        var problems = Scripts.Concatenations
            .Select(c => c.File + " line " + c.Line.ToString(CultureInfo.InvariantCulture) +
                " starts with " + c.Head + ": " + c.Text)
            .ToList();

        Assert.IsEmpty(
            problems,
            "A '+' that builds text starts with something that is not text. Put [string] in front of the first " +
            "operand, or write a literal first, or PowerShell will throw instead of building the text:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // The same failure in another shape. A report leaves a member out whenever the read behind it
    // did not answer, so Get-Field and its callers return $null, and calling a method on $null
    // throws "You cannot call a method on a null-valued expression". The script's own catch turns
    // that into "run: fail" with the rest of the sitting lost, exactly where the evidence matters
    // most. A command result therefore has to be made a string before a method is called on it.
    [TestMethod]
    public void NoMethodIsCalledStraightOnACommandResult()
    {
        var problems = Scripts.MethodCalls
            .Where(c => !CommandsThatNeverReturnNull.Contains(c.Command))
            .Select(c => c.File + " line " + c.Line.ToString(CultureInfo.InvariantCulture) +
                " calls ." + c.Method + "() on " + c.Command + ": " + c.Text)
            .ToList();

        Assert.IsEmpty(
            problems,
            "A method is called straight on what a command returned, and that command can return $null, " +
            "which throws at run time. Write ('' + (...)) or [string](...) around it first:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // The third shape, and the one that has come back twice after being fixed twice.
    //
    // PowerShell unrolls what a function writes to the pipeline. A helper that ends "return $found"
    // hands the caller $null when $found is empty and the bare element when it holds one, and under
    // Set-StrictMode -Version 2.0 both $null.Count and 'one line'.Count throw PropertyNotFoundStrict.
    // .Length works and .Count does not, so a version bump is not a fix. The throw lands in the
    // script's own catch, which records "run: fail" and abandons every criterion after it, and it
    // fires on the success path: "no matching line" is exactly the answer a boot block that held
    // produces.
    //
    // The rule has two halves, and this holds the caller's half: a .Count, a .Length or an index
    // read on a variable that came straight from a command is written @($lines).Count. That is a
    // no-op when the helper wraps its answer and it repairs the value when somebody takes the
    // wrapping away, so neither half alone has to be right.
    //
    // tools\live-tests\selftest is the other proof, and the one that matters: it runs the scripts.
    [TestMethod]
    public void EveryCountOnACommandResultIsWrappedInAnArraySubexpression()
    {
        var problems = Scripts.CollectionReads
            .Where(r => !r.Wrapped)
            .Select(r => r.File + " line " + r.Line.ToString(CultureInfo.InvariantCulture) + " reads " + r.Member +
                " from $" + r.Variable + ", assigned from a command on line " + r.AssignedLine.ToString(CultureInfo.InvariantCulture) +
                ": " + r.Text)
            .ToList();

        Assert.IsEmpty(
            problems,
            "A count or an index is read straight off what a command returned. PowerShell unrolls that answer, so it " +
            "is $null for no matches and a bare value for one, and both throw under strict mode. Write @($variable) " +
            "round the read:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // The other half of the caller's rule, and the trap in the first half. @() collects what the
    // pipeline emits, and a helper that returns ", $found" has already made its whole list one
    // object by then, so @(Get-EarshotLogLines ...) is a list holding a list and .Count reads 1 for
    // every answer, including none. The call is assigned plainly and the variable is wrapped.
    [TestMethod]
    public void NoCallToAHelperThatWrapsItsAnswerIsPutInAnArraySubexpression()
    {
        var problems = Scripts.ArrayWrappedCalls
            .Where(c => Scripts.WrappingFunctions.Contains(c.Command))
            .Select(c => c.File + " line " + c.Line.ToString(CultureInfo.InvariantCulture) + ": " + c.Text)
            .ToList();

        Assert.IsEmpty(
            problems,
            "A helper that returns its list whole is called inside @(), which wraps it a second time and makes every " +
            "count read 1. Assign the call plainly and put @() round the variable instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));

        Assert.Contains(
            "Get-EarshotLogLines",
            Scripts.WrappingFunctions,
            "Get-EarshotLogLines no longer returns its list with a leading comma, so an empty answer is $null again " +
            "and a single line is a bare string again.");
    }

    // The other half of the same rule. Add-Finding records a value the run measured, and a report
    // writes null for a value it could not read, which is itself the finding. A Mandatory parameter
    // rejects $null with "Cannot bind argument to parameter 'Value' because it is null", so the
    // attribute that lets it through is what keeps a failed device read from ending the test.
    [TestMethod]
    public void AddFindingTakesAValueThatIsNull()
    {
        string module = File.ReadAllText(Path.Combine(ScriptFolder(), "LiveTest.psm1"));
        int start = module.IndexOf("function Add-Finding", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "Add-Finding was not found in LiveTest.psm1.");

        int next = module.IndexOf("\nfunction ", start, StringComparison.Ordinal);
        string body = next < 0 ? module[start..] : module[start..next];
        StringAssert.Contains(
            body,
            "[AllowNull()]$Value",
            "Add-Finding's -Value must carry [AllowNull()], or a finding whose value could not be read throws " +
            "instead of being recorded as not known.");
    }

    // A test that recorded a failure has to say so to whatever started it, not only in result.json.
    // Each script ends with its own exit code, and the launcher passes that code on, so a sitting
    // driven by anything other than a person still sees a failed run as failed.
    [TestMethod]
    public void EveryTestScriptEndsWithItsOwnExitCode()
    {
        foreach (string expected in ExpectedScripts)
        {
            if (expected.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(Path.Combine(ScriptFolder(), expected));
            string wanted = string.Equals(expected, "Run-LiveTests.ps1", StringComparison.OrdinalIgnoreCase)
                ? "exit $LASTEXITCODE"
                : "exit (Get-LiveTestExitCode -Overall $overall)";
            StringAssert.Contains(text, wanted, expected + " does not end with \"" + wanted + "\", so a run that failed would look like one that passed.");
        }
    }

    // A test's own option is only any use if the launcher can pass it. The launcher forwards from a
    // table of its own, so an option added to a script and not to that table is refused for the very
    // test that takes it, and the READMEs that say every option is passed on become wrong.
    [TestMethod]
    public void TheLauncherDeclaresAndForwardsEveryOptionATestDeclares()
    {
        ScriptFile launcher = Scripts.Files.Single(f => string.Equals(f.Name, "Run-LiveTests.ps1", StringComparison.OrdinalIgnoreCase));
        string launcherText = File.ReadAllText(Path.Combine(ScriptFolder(), launcher.Name));
        var declared = launcher.Parameters.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only the tests the owner runs. The scripts under selftest are run by the test project,
        // never by the launcher, and their options are the self-test's own.
        var shipped = ExpectedScripts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (ScriptFile file in Scripts.Files)
        {
            if (file.Name.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.Name, launcher.Name, StringComparison.OrdinalIgnoreCase) ||
                !shipped.Contains(file.Name))
            {
                continue;
            }

            foreach (string parameter in file.Parameters)
            {
                if (string.Equals(parameter, "ExePath", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!declared.Contains(parameter))
                {
                    problems.Add(file.Name + " takes -" + parameter + ", which the launcher does not declare.");
                }
                else if (!launcherText.Contains("Name = '" + parameter + "'", StringComparison.Ordinal))
                {
                    problems.Add(file.Name + " takes -" + parameter + ", which the launcher declares but never forwards.");
                }
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
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
        var fieldReads = new List<FieldRead>();
        var concatenations = new List<Concatenation>();
        var methodCalls = new List<MethodOnCommand>();
        var collectionReads = new List<CollectionRead>();
        var arrayWrappedCalls = new List<ArrayWrappedCall>();
        var wrappingFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement file in Items(document.RootElement, "files"))
        {
            string name = file.GetProperty("name").GetString() ?? "";
            files.Add(new ScriptFile(
                name,
                ReadStrings(file, "errors"),
                ReadStrings(file, "strings"),
                ReadStrings(file, "parameters")));

            foreach (string function in ReadStrings(file, "wrappingFunctions"))
            {
                wrappingFunctions.Add(function);
            }

            foreach (JsonElement read in Items(file, "collectionReads"))
            {
                collectionReads.Add(new CollectionRead(
                    name,
                    read.GetProperty("line").GetInt32(),
                    read.GetProperty("variable").GetString() ?? "",
                    read.GetProperty("member").GetString() ?? "",
                    read.GetProperty("wrapped").GetBoolean(),
                    read.GetProperty("assignedLine").GetInt32(),
                    read.GetProperty("text").GetString() ?? ""));
            }

            foreach (JsonElement wrapped in Items(file, "arrayWrappedCalls"))
            {
                arrayWrappedCalls.Add(new ArrayWrappedCall(
                    name,
                    wrapped.GetProperty("line").GetInt32(),
                    wrapped.GetProperty("command").GetString() ?? "",
                    wrapped.GetProperty("text").GetString() ?? ""));
            }

            foreach (JsonElement read in Items(file, "fieldReads"))
            {
                fieldReads.Add(new FieldRead(name, read.GetProperty("line").GetInt32(), ReadStrings(read, "names")));
            }

            foreach (JsonElement join in Items(file, "concatenations"))
            {
                concatenations.Add(new Concatenation(
                    name,
                    join.GetProperty("line").GetInt32(),
                    join.GetProperty("head").GetString() ?? "",
                    join.GetProperty("text").GetString() ?? ""));
            }

            foreach (JsonElement method in Items(file, "methodCalls"))
            {
                methodCalls.Add(new MethodOnCommand(
                    name,
                    method.GetProperty("line").GetInt32(),
                    method.GetProperty("command").GetString() ?? "",
                    method.GetProperty("method").GetString() ?? "",
                    method.GetProperty("text").GetString() ?? ""));
            }

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

        return new ScriptSet(files, calls, fieldReads, concatenations, methodCalls, collectionReads, arrayWrappedCalls, wrappingFunctions);
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

    private sealed record ScriptSet(
        IReadOnlyList<ScriptFile> Files,
        IReadOnlyList<CommandCall> Calls,
        IReadOnlyList<FieldRead> FieldReads,
        IReadOnlyList<Concatenation> Concatenations,
        IReadOnlyList<MethodOnCommand> MethodCalls,
        IReadOnlyList<CollectionRead> CollectionReads,
        IReadOnlyList<ArrayWrappedCall> ArrayWrappedCalls,
        IReadOnlySet<string> WrappingFunctions);

    // One .Count, .Length or index read on a variable that was assigned straight from a command.
    private sealed record CollectionRead(string File, int Line, string Variable, string Member, bool Wrapped, int AssignedLine, string Text);

    // One @( a single bare command ).
    private sealed record ArrayWrappedCall(string File, int Line, string Command, string Text);

    // One '+' chain that builds text, with the operand it starts with.
    private sealed record Concatenation(string File, int Line, string Head, string Text);

    // One method called straight on the result of a command, for example (Get-Field ...).Trim().
    private sealed record MethodOnCommand(string File, int Line, string Command, string Method, string Text);

    private sealed record ScriptFile(string Name, IReadOnlyList<string> Errors, IReadOnlyList<string> Strings, IReadOnlyList<string> Parameters);

    private sealed record CommandCall(
        string File,
        int Line,
        string Function,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<CommandElement> Elements);

    private sealed record CommandElement(string Kind, string Value);

    // One Get-Field or Get-FieldPath call, with the literal names it reads. A name built from a
    // variable is not recorded, because there is nothing to check it against.
    internal sealed record FieldRead(string File, int Line, IReadOnlyList<string> Names);

    // Parses each script with the PowerShell parser and reports what it found as JSON. ParseFile
    // reads a file and builds a syntax tree; it never runs anything in it, so no live step can
    // happen here. The command lines come from the tree, not from a text search, so a call that is
    // split over several lines is still read whole.
    private const string AnalyserScript = """
        [CmdletBinding()]
        param([Parameter(Mandatory = $true)][string]$Root)

        $ErrorActionPreference = 'Stop'

        # The elements of an argument, whether it was written as one value, as @('a', 'b') or as a
        # bare array literal. @('a', 'b') is an array expression holding an array literal; @('a')
        # holds one expression and no literal. All of them come back as a flat list.
        function Get-ArgumentItems
        {
            param($Argument)

            if ($null -eq $Argument) { return @() }
            if ($Argument -is [System.Management.Automation.Language.ArrayLiteralAst]) { return $Argument.Elements }
            if ($Argument -is [System.Management.Automation.Language.ArrayExpressionAst])
            {
                $items = @()
                foreach ($statement in $Argument.SubExpression.Statements)
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

                return $items
            }

            return @($Argument)
        }

        # The operands of a '+' chain, flattened. ('a' + $b + 'c') is two nested binary expressions,
        # and this returns the three values in the order they were written.
        function Get-PlusOperands
        {
            param($Node)

            if ($Node -is [System.Management.Automation.Language.BinaryExpressionAst] -and $Node.Operator -eq 'Plus')
            {
                return @(Get-PlusOperands -Node $Node.Left) + @(Get-PlusOperands -Node $Node.Right)
            }

            return @($Node)
        }

        # The one command inside an expression, when that is all it is: a bare invocation whose
        # result PowerShell unrolls. ($x + 1), a pipeline and a literal all come back as $null.
        function Get-BareCommand
        {
            param($Expression)

            $node = $Expression
            while ($node -is [System.Management.Automation.Language.ParenExpressionAst]) { $node = $node.Pipeline }
            if ($node -is [System.Management.Automation.Language.PipelineAst])
            {
                if ($node.PipelineElements.Count -ne 1) { return $null }
                $only = $node.PipelineElements[0]
                if ($only -is [System.Management.Automation.Language.CommandAst]) { return $only }
                if ($only -is [System.Management.Automation.Language.CommandExpressionAst]) { return (Get-BareCommand -Expression $only.Expression) }
                return $null
            }

            if ($node -is [System.Management.Automation.Language.CommandAst]) { return $node }
            return $null
        }

        # The variable an array subexpression holds, when it holds nothing else: @($lines) gives
        # 'lines'. Anything else, including @(Get-Something ...), gives $null.
        function Get-WrappedVariableName
        {
            param($Expression)

            if (-not ($Expression -is [System.Management.Automation.Language.ArrayExpressionAst])) { return $null }
            $statements = $Expression.SubExpression.Statements
            if ($statements.Count -ne 1) { return $null }
            if (-not ($statements[0] -is [System.Management.Automation.Language.PipelineAst])) { return $null }
            if ($statements[0].PipelineElements.Count -ne 1) { return $null }
            $only = $statements[0].PipelineElements[0]
            if (-not ($only -is [System.Management.Automation.Language.CommandExpressionAst])) { return $null }
            if (-not ($only.Expression -is [System.Management.Automation.Language.VariableExpressionAst])) { return $null }
            return $only.Expression.VariablePath.UserPath
        }

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
            $fieldReads = @()
            $concatenations = @()
            $methodCalls = @()
            $wrappingFunctions = @()
            $collectionReads = @()
            $arrayWrappedCalls = @()
            # Named apart from the $parameters each Invoke-Earshot call is read into below, which is
            # reset per call: one name for both would report a call's parameters as the script's.
            $scriptParameters = @()
            if ($null -ne $ast)
            {
                # The options a script declares. The launcher has to declare and forward every one of
                # them, or an option the owner passes is refused by a test that does take it.
                if ($null -ne $ast.ParamBlock)
                {
                    foreach ($declared in $ast.ParamBlock.Parameters) { $scriptParameters += $declared.Name.VariablePath.UserPath }
                }

                # A '+' chain that builds text must start with a string, because PowerShell converts
                # the right operand to the type of the left one. An integer on the left of ' of '
                # throws "Cannot convert value" at run time instead of producing text, and a throw
                # inside a criterion aborts the whole test. A [string] cast or .ToString() on the
                # first operand settles the type of the chain.
                # https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_arithmetic_operators
                foreach ($b in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.BinaryExpressionAst] -and $args[0].Operator -eq 'Plus' }, $true))
                {
                    # Only the outermost link of a chain, so one chain is reported once.
                    if ($b.Parent -is [System.Management.Automation.Language.BinaryExpressionAst] -and $b.Parent.Operator -eq 'Plus') { continue }

                    $operands = @(Get-PlusOperands -Node $b)
                    $buildsText = $false
                    foreach ($o in $operands)
                    {
                        if ($o -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                            $o -is [System.Management.Automation.Language.ExpandableStringExpressionAst])
                        {
                            $buildsText = $true
                        }
                    }

                    # No string literal in the chain, so it is arithmetic or an array join, not text.
                    if (-not $buildsText) { continue }

                    $first = $operands[0]
                    if ($first -is [System.Management.Automation.Language.StringConstantExpressionAst]) { continue }
                    if ($first -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) { continue }

                    $head = $first.Extent.Text
                    if ($head.StartsWith('[string]')) { continue }
                    if ($head.EndsWith('.ToString()')) { continue }

                    $whole = ($b.Extent.Text -replace '\s+', ' ')
                    if ($whole.Length -gt 160) { $whole = $whole.Substring(0, 160) + '...' }
                    $concatenations += [ordered]@{ line = $b.Extent.StartLineNumber; head = $head; text = $whole }
                }

                # A method called straight on the result of a command, for example
                # (Get-Field -Object $nodes -Name 'address').ToUpperInvariant(). The helpers return
                # $null for a member a report left out, and $null.Trim() throws "You cannot call a
                # method on a null-valued expression", which aborts the test the same way a parse
                # error would. ('' + (...)) or [string](...) first makes it a string, and $null
                # becomes the empty one.
                # https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_methods
                foreach ($m in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true))
                {
                    $target = $m.Expression
                    if (-not ($target -is [System.Management.Automation.Language.ParenExpressionAst])) { continue }

                    # Only a single bare command inside the brackets. A pipeline such as
                    # ($_ | Out-String) and an expression such as ('' + $x) are settled types already.
                    $inner = $target.Pipeline
                    if (-not ($inner -is [System.Management.Automation.Language.PipelineAst])) { continue }
                    if ($inner.PipelineElements.Count -ne 1) { continue }

                    $element = $inner.PipelineElements[0]
                    if (-not ($element -is [System.Management.Automation.Language.CommandAst])) { continue }

                    $method = ''
                    if ($m.Member -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $method = $m.Member.Value }

                    $whole = ($m.Extent.Text -replace '\s+', ' ')
                    if ($whole.Length -gt 160) { $whole = $whole.Substring(0, 160) + '...' }
                    $methodCalls += [ordered]@{
                        line = $m.Extent.StartLineNumber
                        command = ('' + $element.GetCommandName())
                        method = $method
                        text = $whole
                    }
                }

                # The third shape of the same failure, and the one that has come back twice. A
                # function that ends "return $found" has its answer unrolled by the pipeline, so the
                # caller gets $null for an empty list and a bare value for a list of one, and
                # .Count on either throws under strict mode. The rule is: the function returns
                # ", $found" so nothing is unrolled, and the caller puts @() round the VARIABLE
                # wherever it reads .Count, .Length or an index.
                #
                # Three things are collected here. First, the functions that already wrap their
                # answer. Second, every .Count, .Length or index read on a variable that was
                # assigned straight from a command, with whether that read is wrapped. Third,
                # every @( one bare command ), because @() round a call that already wraps its
                # answer wraps it twice and .Count then reads 1 for every answer, including none.
                # https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_arrays
                foreach ($f in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
                {
                    foreach ($r in $f.Body.FindAll({ $args[0] -is [System.Management.Automation.Language.ReturnStatementAst] }, $true))
                    {
                        $value = $r.Pipeline
                        if ($value -is [System.Management.Automation.Language.PipelineAst] -and $value.PipelineElements.Count -eq 1)
                        {
                            $value = $value.PipelineElements[0]
                        }

                        if ($value -is [System.Management.Automation.Language.CommandExpressionAst]) { $value = $value.Expression }
                        if ($value -is [System.Management.Automation.Language.ArrayLiteralAst] -and $value.Elements.Count -eq 1)
                        {
                            if (-not ($wrappingFunctions -contains $f.Name)) { $wrappingFunctions += $f.Name }
                        }
                    }
                }

                # Which variables were assigned straight from a command, and so hold whatever the
                # pipeline made of that command's answer.
                $fromCommand = @{}
                foreach ($a in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
                {
                    if (-not ($a.Left -is [System.Management.Automation.Language.VariableExpressionAst])) { continue }
                    if ($null -eq (Get-BareCommand -Expression $a.Right)) { continue }
                    $fromCommand[$a.Left.VariablePath.UserPath] = $a.Extent.StartLineNumber
                }

                foreach ($m in $ast.FindAll({
                            $args[0] -is [System.Management.Automation.Language.MemberExpressionAst] -or
                            $args[0] -is [System.Management.Automation.Language.IndexExpressionAst] }, $true))
                {
                    # A method call is the other guard's business, not this one.
                    if ($m -is [System.Management.Automation.Language.InvokeMemberExpressionAst]) { continue }

                    $member = 'an index'
                    if ($m -is [System.Management.Automation.Language.IndexExpressionAst])
                    {
                        # Only a numbered index. $table['name'] and $table[$key] are dictionary
                        # lookups, which say nothing about whether the value is a list.
                        $index = $m.Index
                        $numbered = ($index -is [System.Management.Automation.Language.BinaryExpressionAst])
                        if ($index -is [System.Management.Automation.Language.ConstantExpressionAst] -and
                            -not ($index -is [System.Management.Automation.Language.StringConstantExpressionAst]))
                        {
                            $numbered = $true
                        }

                        if (-not $numbered) { continue }
                        $target = $m.Target
                    }
                    else
                    {
                        $target = $m.Expression
                        if (-not ($m.Member -is [System.Management.Automation.Language.StringConstantExpressionAst])) { continue }
                        $member = $m.Member.Value
                        if ($member -ne 'Count' -and $member -ne 'Length') { continue }
                    }

                    # $x[0].Count is read through the same rule as $x.Count.
                    while ($target -is [System.Management.Automation.Language.IndexExpressionAst]) { $target = $target.Target }

                    $wrapped = $false
                    $name = $null
                    if ($target -is [System.Management.Automation.Language.VariableExpressionAst])
                    {
                        $name = $target.VariablePath.UserPath
                    }
                    else
                    {
                        $name = Get-WrappedVariableName -Expression $target
                        if ($null -ne $name) { $wrapped = $true }
                    }

                    if ($null -eq $name) { continue }
                    if (-not $fromCommand.ContainsKey($name)) { continue }

                    $whole = ($m.Extent.Text -replace '\s+', ' ')
                    if ($whole.Length -gt 160) { $whole = $whole.Substring(0, 160) + '...' }
                    $collectionReads += [ordered]@{
                        line = $m.Extent.StartLineNumber
                        variable = $name
                        member = $member
                        wrapped = $wrapped
                        assignedLine = $fromCommand[$name]
                        text = $whole
                    }
                }

                foreach ($a in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.ArrayExpressionAst] }, $true))
                {
                    $statements = $a.SubExpression.Statements
                    if ($statements.Count -ne 1) { continue }
                    $inner = Get-BareCommand -Expression $statements[0]
                    if ($null -eq $inner) { continue }

                    $whole = ($a.Extent.Text -replace '\s+', ' ')
                    if ($whole.Length -gt 160) { $whole = $whole.Substring(0, 160) + '...' }
                    $arrayWrappedCalls += [ordered]@{
                        line = $a.Extent.StartLineNumber
                        command = ('' + $inner.GetCommandName())
                        text = $whole
                    }
                }

                # Every literal name a script reads out of a JSON report, so that each one can be
                # checked against the writer that is supposed to produce it.
                foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true))
                {
                    $name = $c.GetCommandName()
                    if ($name -ne 'Get-Field' -and $name -ne 'Get-FieldPath') { continue }

                    $elements = $c.CommandElements
                    for ($i = 1; $i -lt $elements.Count; $i++)
                    {
                        $element = $elements[$i]
                        if (-not ($element -is [System.Management.Automation.Language.CommandParameterAst])) { continue }
                        if ($element.ParameterName -ne 'Name' -and $element.ParameterName -ne 'Path') { continue }

                        $argument = $element.Argument
                        if ($null -eq $argument -and $i + 1 -lt $elements.Count) { $argument = $elements[$i + 1] }

                        $names = @()
                        foreach ($item in (Get-ArgumentItems -Argument $argument))
                        {
                            if ($item -is [System.Management.Automation.Language.StringConstantExpressionAst])
                            {
                                $names += $item.Value
                            }
                        }

                        if ($names.Count -gt 0)
                        {
                            $fieldReads += [ordered]@{ line = $c.Extent.StartLineNumber; names = $names }
                        }
                    }
                }

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
                        foreach ($item in (Get-ArgumentItems -Argument $commandArgument))
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
                parameters = $scriptParameters
                calls = $calls
                fieldReads = $fieldReads
                concatenations = $concatenations
                methodCalls = $methodCalls
                wrappingFunctions = $wrappingFunctions
                collectionReads = $collectionReads
                arrayWrappedCalls = $arrayWrappedCalls
                strings = $strings
            }
        }

        ([ordered]@{ root = $Root; files = $files } | ConvertTo-Json -Depth 8)
        """;
}
