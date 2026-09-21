using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Today only three halves (SandboxFakesSmokeTests, Test10VariantSmokeTests) are ever driven
// through the window's own real protocol over the shipped scripts. This drives every script and
// half tools\live-tests\selftest\Invoke-SelfTest.ps1's own $tests table covers (16 scripts, 22
// halves), each against the fakes, through the same real ChildRunner/Invoke-GuiHalf machinery,
// answering exactly the way Fakes.psm1's own Get-FakeAnswer/Get-FakeNote would for the "one" case,
// and checks three things for every half: (a) it is driven to a recorded result.json, (b) the row
// state the window derives from that file (StateDeriver through EvidenceStore) is the one the file
// implies, leftAtRest included, and (c) the evidence written matches what expectations.psd1 says
// for that script, half and the "one" case: the same criterion ids, the same outcome for every
// criterion whose outcome is pinned, the same overall and the same finding names. Nothing here is
// copied out of Invoke-SelfTest.ps1, Fakes.psm1 or expectations.psd1 by hand: SelfTestFixtures
// reads all three through the small exporter scripts beside this one
// (tools\live-tests\gui\selftest\Export-*.ps1), so a change to any of them is picked up here too.
//
// Known exceptions, each with its own obstacle:
//
//   14-set-device-refusal|first: only ExePath, RunRoot, Resume, Variant, OfferUninstall and
//   AllowPlanB ever reach the target script through the window's own drivers (Invoke-GuiHalf.ps1's
//   and Run-GuiHalfAgainstFakes.ps1's own Get-EarshotTwScriptParameters), so -SpeakerAddress can
//   never be forwarded. refuses-protected-move therefore always reads inconclusive through the
//   window (no second device was ever given), never expectations.psd1's pinned "pass" for the
//   "one" case. Still driven; the mismatch itself is asserted below (CriterionOutcomeOverrides),
//   so a future change that starts or stops matching expectations.psd1 is still caught.
//
//   Test 10, variants 2 to 5 (tools\live-tests\selftest\Fakes.psm1's own StartStates has an entry
//   only for "10-shutdown-messages-v1"): these are not part of Invoke-SelfTest.ps1's own $tests
//   table at all (it only ever drives variant 1), so they are outside this sweep by construction,
//   the same way Test10VariantSmokeTests's header comment records it. The window's own row detail
//   area now says so for a sandboxed run (Test10VariantSandboxNoteTests, Ui\Copy.cs's
//   Test10VariantNotSandboxTestable).
//
// Timing, not correctness: 03-AllowPages.ps1's first half loops on the real, unstubbed
// Wait-Seconds for its own $WatchSeconds (120 s by default; the window cannot shorten it either,
// for the same forwarding reason as -SpeakerAddress above), because nothing in the fake world ever
// moves the render endpoint for a plain "diag gate allow". Every criterion it records is decided by
// whether the endpoint ever went Active, never by how long the wait was, so the evidence still
// matches expectations.psd1 exactly; it is simply the slow half, and is given a longer timeout
// and driven alongside everything else in parallel rather than excepted.
[TestClass]
public sealed class AllScriptsAndHalvesThroughWindowTests
{
    private static readonly TimeSpan DefaultHalfTimeout = TimeSpan.FromSeconds(60);

    // 03-AllowPages.ps1 only: see the class comment's "Timing, not correctness" note.
    private static readonly TimeSpan LongHalfTimeout = TimeSpan.FromSeconds(200);

    private const int ExpectedScriptCount = 16;
    private const int ExpectedHalfCount = 22;

    // 14-set-device-refusal|first only: see the class comment's first known exception.
    private static readonly Dictionary<string, string> CriterionOutcomeOverrides =
        new(StringComparer.Ordinal) { ["refuses-protected-move"] = "inconclusive" };

    [TestMethod]
    public void EveryScriptAndHalfIsDrivenThroughTheWindowWithMatchingEvidence()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so this settles nothing.");
        }

        string repoRoot = RepositoryLocator.RepositoryRoot();
        string scriptsFolder = Path.Combine(repoRoot, "tools", "live-tests");
        string driverScript = Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Run-GuiHalfAgainstFakes.ps1");
        Assert.IsTrue(File.Exists(driverScript), "The sandbox driver was not found at " + driverScript + ".");

        IReadOnlyList<SelfTestPlanRow> plan = SelfTestFixtures.LoadPlan(repoRoot, host);
        Assert.AreEqual(ExpectedScriptCount, plan.Count, "Invoke-SelfTest.ps1's own $tests table names a different number of scripts than expected.");
        Assert.AreEqual(ExpectedHalfCount, plan.Sum(row => row.Halves.Count), "Invoke-SelfTest.ps1's own $tests table names a different number of halves than expected.");

        var owner = new FakeOwnerAnswers(SelfTestFixtures.LoadOwnerTables(repoRoot, host));
        IReadOnlyDictionary<string, ExpectationsPlanRow> expectations = SelfTestFixtures.LoadExpectations(repoRoot, host);

        IReadOnlyList<ManifestRow> manifestRows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        Dictionary<string, DisplayRow> displayRowsById = DisplayRow.Flatten(manifestRows)
            .GroupBy(row => row.TestId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        IReadOnlyList<string> knownScripts = plan.Select(row => row.Script).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var expectedHalves = new HashSet<string>(
            plan.SelectMany(row => row.Halves.Select(half => row.Script + "|" + half)), StringComparer.Ordinal);
        var covered = new ConcurrentBag<string>();
        var problems = new ConcurrentBag<string>();

        int maxParallelRows = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
        using var gate = new SemaphoreSlim(maxParallelRows);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Task[] tasks = plan.Select(row => Task.Run(() =>
        {
            gate.Wait();
            try
            {
                DriveRow(row, host, driverScript, scriptsFolder, repoRoot, owner, expectations, displayRowsById, knownScripts, covered, problems);
            }
            finally
            {
                gate.Release();
            }
        })).ToArray();
        Task.WaitAll(tasks);
        stopwatch.Stop();

        foreach (string half in expectedHalves.Except(covered))
        {
            problems.Add("Never driven to a recorded result.json: " + half + ".");
        }

        Console.WriteLine(
            "AllScriptsAndHalvesThroughWindowTests: " + covered.Count + "/" + expectedHalves.Count +
            " halves driven through the window in " + stopwatch.Elapsed + " wall time.");

        List<string> sortedProblems = problems.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.IsFalse(
            sortedProblems.Count > 0,
            "Driving every script and half in Invoke-SelfTest.ps1's own $tests table through the window found:" +
            Environment.NewLine + string.Join(Environment.NewLine, sortedProblems));
    }

    // One script, every half it has, in its own sandbox folder: the first half, then (for a
    // resumable test) the resume half in the same run root, exactly as StartResumedSecondHalf
    // itself only ever resumes in place.
    private static void DriveRow(
        SelfTestPlanRow row, string host, string driverScript, string scriptsFolder, string repoRoot,
        FakeOwnerAnswers owner, IReadOnlyDictionary<string, ExpectationsPlanRow> expectations,
        Dictionary<string, DisplayRow> displayRowsById, IReadOnlyList<string> knownScripts,
        ConcurrentBag<string> covered, ConcurrentBag<string> problems)
    {
        string scriptPath = Path.Combine(scriptsFolder, row.Script);
        if (!File.Exists(scriptPath))
        {
            problems.Add(row.Script + ": the script was not found at " + scriptPath + ".");
            return;
        }

        if (!displayRowsById.TryGetValue(row.Id, out DisplayRow? displayRow))
        {
            problems.Add(row.Script + ": Data\\tests.json has no row whose testId is \"" + row.Id + "\", so the window's own row state cannot be checked for it.");
            return;
        }

        TestRowSpec spec = displayRow.ToSpec();
        int variant = ReadVariant(row.Extra);

        using var sandbox = new Earshot.Tests.TempFolder();
        var sandboxOptions = new SandboxOptions { Folder = sandbox.Path };
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string runRoot = Path.Combine(liveTestRoot, "20260921T000000Z");
        string exePath = Path.Combine(sandbox.Path, "nowhere", "Earshot.exe");
        TimeSpan timeout = row.Number == "03" ? LongHalfTimeout : DefaultHalfTimeout;

        for (int index = 0; index < row.Halves.Count; index++)
        {
            string half = row.Halves[index];
            bool isLastHalf = index == row.Halves.Count - 1;
            string where = row.Script + " " + half;

            (ParsedResult? result, List<string> haltProblems) = DriveHalf(
                row, half, host, driverScript, scriptPath, exePath, runRoot, variant, sandboxOptions, owner, timeout);
            foreach (string problem in haltProblems)
            {
                problems.Add(problem);
            }

            if (result is null)
            {
                return;
            }

            covered.Add(row.Script + "|" + half);

            foreach (string problem in CompareToExpectations(row, half, result, expectations))
            {
                problems.Add(problem);
            }

            foreach (string problem in CheckDerivedState(spec, liveTestRoot, isLastHalf, result, where))
            {
                problems.Add(problem);
            }

            if (!isLastHalf)
            {
                foreach (string problem in CheckPendingRun(spec, liveTestRoot, runRoot, row.Id, repoRoot, knownScripts, where))
                {
                    problems.Add(problem);
                }
            }
        }
    }

    private static int ReadVariant(IReadOnlyList<string> extra)
    {
        foreach (string entry in extra)
        {
            int split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split < 1)
            {
                continue;
            }

            string name = entry[..split];
            string value = entry[(split + 1)..];
            if (string.Equals(name, "Variant", StringComparison.Ordinal) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        // Every other name in a row's own Extra (WatchSeconds, WatchMinutes, SpeakerAddress) is
        // not one Invoke-GuiHalf.ps1/Run-GuiHalfAgainstFakes.ps1 ever forwards to the target
        // script (see the class comment's known exceptions): the script runs with its own shipped
        // default for it instead, exactly as a real window run would.
        return 0;
    }

    // Drives one half through the real protocol: ChildRunner, the sandbox twin of the production
    // driver, and the target script's own real prompt helpers. Returns the parsed result (null if
    // the half could not be driven to one) and whatever went wrong along the way.
    private static (ParsedResult? Result, List<string> Problems) DriveHalf(
        SelfTestPlanRow row, string half, string host, string driverScript, string scriptPath, string exePath,
        string runRoot, int variant, SandboxOptions sandboxOptions, FakeOwnerAnswers owner, TimeSpan timeout)
    {
        var problems = new List<string>();
        string where = row.Script + " " + half;
        bool resume = string.Equals(half, "resume", StringComparison.Ordinal);

        var extraArguments = new List<(string Name, string Value)>
        {
            ("SandboxRoot", sandboxOptions.Folder),
            ("TestId", row.Id),
            ("Case", "one"),
        };

        var messages = new BlockingCollection<ChildMessage>();
        var transcript = new ConcurrentQueue<string>();

        using var runner = new ChildRunner(
            host, driverScript, scriptPath, exePath, runRoot,
            resume: resume, variant: variant, offerUninstall: false, allowPlanB: false,
            environmentOverrides: sandboxOptions.ChildEnvironment, extraArguments: extraArguments);
        runner.MessageReceived += message => messages.Add(message);
        runner.TranscriptLine += line => transcript.Enqueue(line);
        runner.Start();

        int? exitCode = null;
        bool sawExit = false;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!messages.TryTake(out ChildMessage? message, deadline - DateTime.UtcNow))
            {
                break;
            }

            if (message.Kind == ChildMessageKind.Prompt)
            {
                string reply;
                try
                {
                    reply = AnswerPrompt(message, owner);
                }
                catch (InvalidOperationException ex)
                {
                    problems.Add(where + ": " + ex.Message);
                    runner.Abort(message.Seq);
                    continue;
                }

                runner.ReplyFromOwnerClick(message.Seq, reply);
            }
            else if (message.Kind == ChildMessageKind.Exit)
            {
                exitCode = message.ExitCode;
                sawExit = true;
                break;
            }
            else if (message.Kind == ChildMessageKind.Crash)
            {
                problems.Add(where + ": the sandboxed run crashed: " + message.Text + " Transcript: " + string.Join(" | ", transcript));
                return (null, problems);
            }
            else if (message.Kind == ChildMessageKind.Unreadable)
            {
                problems.Add(where + ": an unreadable protocol message arrived: " + message.Text);
            }
        }

        if (!sawExit)
        {
            problems.Add(where + ": no exit message arrived within " + timeout + ". Transcript: " + string.Join(" | ", transcript));
            return (null, problems);
        }

        if (!runner.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            problems.Add(where + ": the child did not exit after reporting its own exit code.");
            return (null, problems);
        }

        if (exitCode is < 0 or > 2)
        {
            problems.Add(where + ": the run exited " + exitCode + ", which is not 0, 1 or 2.");
        }

        string resultPath = Path.Combine(runRoot, row.Id, "result.json");
        (ParsedResult? result, string? failure) = EvidenceStore.TryReadResult(resultPath, row.Id);
        if (result is null)
        {
            problems.Add(where + ": result.json did not parse at " + resultPath + ": " + failure);
            return (null, problems);
        }

        if (result.Errors.Count != 0)
        {
            problems.Add(where + ": the run recorded " + result.Errors.Count + " error(s), which the \"one\" case never should: " + string.Join(" | ", result.Errors));
        }

        return (result, problems);
    }

    // The reply grammar each helper expects (SandboxFakesSmokeTests/Test10VariantSmokeTests):
    // Wait-Owner's Yes is an empty line (the fake machine is moved automatically by
    // Run-GuiHalfAgainstFakes.ps1's own Read-Host wrapper, from the Text it was given, not from
    // what is typed back). Read-Answer and Read-Note are answered from Fakes.psm1's own tables
    // (FakeOwnerAnswers), exactly as Get-FakeAnswer/Get-FakeNote would for the "one" case.
    // Everything else (Show-Preconditions, Confirm-Step) takes a plain "y".
    private static string AnswerPrompt(ChildMessage message, FakeOwnerAnswers owner)
    {
        switch (message.Caller)
        {
            case "Wait-Owner":
                return string.Empty;

            case "Read-Answer":
            {
                string question = message.Bound.TryGetValue("Question", out string? boundQuestion) ? boundQuestion : message.Prompt ?? string.Empty;
                IReadOnlyList<string> options = new[] { "yes", "no", "unsure" };
                if (message.Bound.TryGetValue("Options", out string? optionsRaw))
                {
                    using JsonDocument document = JsonDocument.Parse(optionsRaw);
                    options = document.RootElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
                }

                return owner.Answer(question, options);
            }

            case "Read-Note":
            {
                string question = message.Bound.TryGetValue("Question", out string? boundQuestion) ? boundQuestion : message.Prompt ?? string.Empty;
                return owner.Note(question);
            }

            default:
                return "y";
        }
    }

    // (c): the same set of criterion ids, the same outcome for every criterion whose outcome
    // expectations.psd1 pins (not "any"), the same overall, and the same finding names, for the
    // "one" case expectations.psd1 gives this script and half.
    private static List<string> CompareToExpectations(
        SelfTestPlanRow row, string half, ParsedResult result, IReadOnlyDictionary<string, ExpectationsPlanRow> expectations)
    {
        var problems = new List<string>();
        string where = row.Script + " " + half;
        string key = row.Id + "|" + half;

        if (!expectations.TryGetValue(key, out ExpectationsPlanRow? expected))
        {
            problems.Add(where + ": expectations.psd1 has no \"one\" entry for \"" + key + "\".");
            return problems;
        }

        bool isKnownException = string.Equals(row.Id, "14-set-device-refusal", StringComparison.Ordinal) && string.Equals(half, "first", StringComparison.Ordinal);

        // The known exception overrides refuses-protected-move to inconclusive (below), which
        // Complete-LiveTestRun's own recompute rule (any inconclusive criterion, with no failing
        // one, makes the overall inconclusive) turns into an overall of inconclusive rather than
        // expectations.psd1's plain "pass" for the "one" case. Allowed here, not silently ignored:
        // a run that still came out "pass" despite the override (SpeakerAddress reaching the
        // script after all) would still be flagged as unexpected below.
        IReadOnlyList<string> allowedOverall = isKnownException ? new[] { "pass", "inconclusive" } : expected.Overall;
        if (!allowedOverall.Contains(result.Overall, StringComparer.Ordinal))
        {
            problems.Add(where + ": the run came out " + result.Overall + ", and expectations.psd1's \"one\" case implies " + string.Join(" or ", allowedOverall) + ".");
        }

        var actualIds = new HashSet<string>(result.Criteria.Select(c => c.Id).Where(id => id != "run"), StringComparer.Ordinal);
        var expectedIds = new HashSet<string>(expected.Criteria.Keys, StringComparer.Ordinal);

        foreach (string id in actualIds)
        {
            if (!expectedIds.Contains(id))
            {
                problems.Add(where + ": \"" + id + "\" was recorded and expectations.psd1's \"one\" case does not name it.");
            }
        }

        foreach (string id in expectedIds)
        {
            if (!actualIds.Contains(id))
            {
                problems.Add(where + ": the criterion \"" + id + "\" was never recorded.");
                continue;
            }

            string actualOutcome = result.Criteria.First(c => c.Id == id).Outcome;
            string wantedOutcome = expected.Criteria[id];
            if (isKnownException && CriterionOutcomeOverrides.TryGetValue(id, out string? overridden))
            {
                wantedOutcome = overridden;
            }

            if (wantedOutcome == "any")
            {
                continue;
            }

            if (actualOutcome != wantedOutcome)
            {
                string note = isKnownException && CriterionOutcomeOverrides.ContainsKey(id)
                    ? " (adjusted for the known exception: -SpeakerAddress cannot be forwarded through the window)"
                    : string.Empty;
                problems.Add(where + ": \"" + id + "\" came out " + actualOutcome + ", and expectations.psd1's \"one\" case implies " + wantedOutcome + "." + note);
            }
        }

        if (result.Criteria.Any(c => c.Id == "run"))
        {
            problems.Add(where + ": the script recorded its own catch-all \"run\" criterion, so it stopped early.");
        }

        var actualFindingNames = new HashSet<string>(result.Findings.Select(f => f.Name), StringComparer.Ordinal);
        if (expected.FindingNames is { } exhaustiveNames)
        {
            var wantedNames = new HashSet<string>(exhaustiveNames, StringComparer.Ordinal);
            foreach (string name in wantedNames)
            {
                if (!actualFindingNames.Contains(name))
                {
                    problems.Add(where + ": the finding \"" + name + "\" was never recorded.");
                }
            }

            foreach (string name in actualFindingNames)
            {
                if (!wantedNames.Contains(name))
                {
                    problems.Add(where + ": the finding \"" + name + "\" was recorded and expectations.psd1's \"one\" case does not name it.");
                }
            }
        }

        if (expected.FindingsIncludeNames is { } includedNames)
        {
            foreach (string name in includedNames)
            {
                if (!actualFindingNames.Contains(name))
                {
                    problems.Add(where + ": the finding \"" + name + "\" was never recorded.");
                }
            }
        }

        return problems;
    }

    // (b): the row state the window derives from the file it just wrote (StateDeriver through
    // EvidenceStore, read fresh off disk, never the in-memory ParsedResult) is the one the file
    // implies, and leftAtRest shown equals the finding.
    private static List<string> CheckDerivedState(TestRowSpec spec, string liveTestRoot, bool isLastHalf, ParsedResult result, string where)
    {
        var problems = new List<string>();
        IReadOnlyList<RunEvidence> evidence = EvidenceStore.LoadEvidence(liveTestRoot, spec.TestId);
        DerivedRowState derived = StateDeriver.Derive(spec, evidence, chosenExePath: null, chosenExeLastWriteUtc: null);

        if (derived.LeftAtRest != result.LeftAtRest)
        {
            problems.Add(
                where + ": the window's own leftAtRest (\"" + (derived.LeftAtRest ?? "null") +
                "\") does not equal the finding result.json actually recorded (\"" + (result.LeftAtRest ?? "null") + "\").");
        }

        if (!isLastHalf)
        {
            RowStateKind expectedWaiting = spec.PowerCycleRequirement == PowerCycleRequirement.FullShutDown
                ? RowStateKind.WaitingForShutDown
                : RowStateKind.WaitingForRestart;
            if (derived.Kind != expectedWaiting)
            {
                problems.Add(where + ": after this half, which wrote resume.txt, the window derived " + derived.Kind + " rather than " + expectedWaiting + ".");
            }

            return problems;
        }

        RowStateKind expectedKind = result.Overall switch
        {
            "pass" => RowStateKind.Passed,
            "fail" => RowStateKind.Failed,
            _ => RowStateKind.Inconclusive,
        };
        if (result.StoppedEarly)
        {
            expectedKind = RowStateKind.Failed;
        }

        if (derived.Kind != expectedKind)
        {
            problems.Add(where + ": result.json's own overall (\"" + result.Overall + "\") implies " + expectedKind + ", but the window derived " + derived.Kind + ".");
        }

        return problems;
    }

    // Generic over every two-half row (not only test 10): the pending-run detection
    // (PendingRunFinder) and resume.txt parsing (ResumeFile) StartResumedSecondHalf itself relies
    // on, exercised for real between the two halves this sweep drives.
    private static List<string> CheckPendingRun(
        TestRowSpec spec, string liveTestRoot, string runRoot, string testId, string repoRoot, IReadOnlyList<string> knownScripts, string where)
    {
        var problems = new List<string>();
        PendingRun? pending = PendingRunFinder.Find(spec, liveTestRoot);
        string testFolder = Path.Combine(runRoot, testId);
        if (pending is null)
        {
            problems.Add(where + ": no pending run was found after this half, though it wrote resume.txt.");
            return problems;
        }

        if (!string.Equals(pending.Folder, testFolder, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(where + ": the pending run's folder (" + pending.Folder + ") is not this row's own run folder (" + testFolder + ").");
        }

        bool parsed = ResumeFile.TryParse(
            Path.Combine(testFolder, "resume.txt"), repoRoot, liveTestRoot, knownScripts, out ResumeInstruction? instruction, out string? reason);
        if (!parsed)
        {
            problems.Add(where + ": resume.txt did not parse: " + reason);
            return problems;
        }

        if (!string.Equals(instruction!.RunRoot, runRoot, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(where + ": resume.txt's own -RunRoot (" + instruction.RunRoot + ") is not this row's own run root (" + runRoot + ").");
        }

        return problems;
    }
}
