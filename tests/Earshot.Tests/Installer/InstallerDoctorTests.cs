using System.Text;
using Earshot.Tests.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// The Help, Doctor and Fix actions of installer\earshot.ps1, run for real in Windows PowerShell 5.1 and PowerShell 7 against
// the local release feed and a stub install root. Doctor and Fix download the latest release, check it as Uninstall does, and
// run the unpacked program's own word unelevated. The run of that word is a hook in most rows (the boundary), and one row runs
// it for real against a harmless Windows program. None of them raises a prompt, asks for elevation or touches a device.
[TestClass]
public sealed class InstallerDoctorTests
{
    private const string FakeReport = "Earshot doctor (a stand-in report)";

    private static string GuideFile()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "docs", "ai-guide.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs\\ai-guide.md was not found above " + AppContext.BaseDirectory);
    }

    private static string ReadmeFile() => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(GuideFile()))!, "README.md");

    private static string Normal(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    // The driver: hooks that stand in for the program's version and its run, the stub roots, the script called with the action.
    // verbProbe is PowerShell run inside the stand-in for the program's word, while the work folder exists; $out is the report's path in it.
    private static InstallerRun Run(InstallerWorld world, ShellKind shell, string action, string version = "1.5.0", int exitCode = 0, bool realRun = false, string verbProbe = "")
    {
        var d = new StringBuilder();
        d.AppendLine("$ErrorActionPreference = 'Stop'");
        d.AppendLine("$global:NoteFile = " + InstallerWorld.Q(world.LogFile));
        d.AppendLine(InstallerWorld.NoteFunction);
        d.AppendLine("$h = @{}");
        d.AppendLine("$h.IsElevated = { $false }");
        d.AppendLine("$h.CanAsk = $false");
        d.AppendLine("$h.Elevate = { param($exe, $line) Note ('Elevate|' + $exe); [pscustomobject]@{ ExitCode = 0 } }");
        d.AppendLine("$h.StartTray = { param($exe) Note ('StartTray|' + $exe) }");
        d.AppendLine("$h.FindTray = { @() }");
        d.AppendLine("$h.ExitTray = { param($t) Note ('ExitTray|' + $t.Id) }");
        d.AppendLine("$h.ExeVersion = { param($exe) Note ('ExeVersion|' + $exe); " + InstallerWorld.Q(version) + " }");
        if (!realRun)
        {
            d.AppendLine("$h.RunVerb = { param($exe, $verb, $out) Note ('RunVerb|' + $verb + '|' + $exe); " + verbProbe + " [IO.File]::WriteAllText($out, " + InstallerWorld.Q(FakeReport + " for " + action) + "); " + exitCode + " }");
        }

        d.AppendLine("$roots = @{ Install = " + InstallerWorld.Q(world.Install) + "; Machine = " + InstallerWorld.Q(world.Machine) + "; UserPrograms = " + InstallerWorld.Q(world.UserPrograms) +
                     "; Roaming = " + InstallerWorld.Q(world.Roaming) + "; Local = " + InstallerWorld.Q(world.Local) + "; Temp = " + InstallerWorld.Q(world.TempRoot) +
                     "; RunKey = " + InstallerWorld.Q(world.RunKey) + " }");
        d.Append(InstallerWorld.CallScript("-Action " + action + " -Feed " + InstallerWorld.Q(world.Feed.FeedAddress) + " -Roots $roots -TestHooks $h"));
        world.Spec.Raw = true;
        world.Spec.RawScript = d.ToString();
        return world.Run(shell);
    }

    // ----- Doctor and Fix: a checked download, then the program's word -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "Doctor", "doctor")]
    [DataRow(ShellKind.WindowsPowerShell, "Fix", "fix")]
    [DataRow(ShellKind.PowerShell7, "Doctor", "doctor")]
    [DataRow(ShellKind.PowerShell7, "Fix", "fix")]
    public void DoctorAndFixWorkWithEarshotNotInstalledByRunningTheCheckedDownloadsOwnWord(ShellKind shell, string action, string verb)
    {
        using var world = new InstallerWorld();

        InstallerRun run = Run(world, shell, action);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, "The download matches its checksum.");
        string[] ran = run.CallsNamed("RunVerb");
        Assert.HasCount(1, ran, run.Describe());
        StringAssert.StartsWith(ran[0], "RunVerb|" + verb + "|");
        StringAssert.EndsWith(ran[0], Path.Combine("app", "Earshot", "Earshot.exe"), "The word is run on the unpacked, checked copy.");
        CollectionAssert.Contains(run.Lines, FakeReport + " for " + action, "What the program printed is shown.");
        Assert.IsEmpty(run.CallsNamed("Elevate"), "Never elevated.");
        Assert.AreEqual(1, world.Feed.ZipDownloads);
        Assert.IsFalse(File.Exists(world.InstalledExe), "Nothing was installed.");
    }

    // The work folder is private to the person and SYSTEM: a TEMP that is shared (C:\Temp lets every signed-in user write) must not
    // let another user swap a file between its check and its use. The rules are read inside the run, while the folder exists.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheWorkFolderIsAFreshGuidFolderThatOnlyThePersonAndSystemCanUse(ShellKind shell)
    {
        using var world = new InstallerWorld();
        const string probe =
            "$f = Split-Path -Parent $out; $a = Get-Acl -LiteralPath $f; Note ('Folder|' + (Split-Path -Leaf $f)); Note ('Protected|' + $a.AreAccessRulesProtected); " +
            "foreach ($r in $a.Access) { Note ('Rule|' + $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value + '|' + $r.IsInherited + '|' + $r.AccessControlType) };";

        InstallerRun run = Run(world, shell, "Doctor", verbProbe: probe);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string folder = run.Calls.Single(c => c.StartsWith("Folder|", StringComparison.Ordinal))["Folder|".Length..];
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(folder, "^earshot-[0-9a-f]{32}$"), "A whole GUID, not a guessable part of one: " + folder);
        Assert.AreEqual("Protected|True", run.Calls.Single(c => c.StartsWith("Protected|", StringComparison.Ordinal)), "No rule is inherited from TEMP.");
        string person = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string[] rules = run.Calls.Where(c => c.StartsWith("Rule|", StringComparison.Ordinal)).Select(c => c["Rule|".Length..]).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "S-1-5-18|False|Allow", person + "|False|Allow" }.OrderBy(c => c, StringComparer.Ordinal).ToArray(), rules, run.Describe());
        Assert.IsEmpty(Directory.GetDirectories(world.TempRoot, "earshot-*"), "The folder is removed at the end.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "Doctor")]
    [DataRow(ShellKind.WindowsPowerShell, "Fix")]
    [DataRow(ShellKind.PowerShell7, "Doctor")]
    [DataRow(ShellKind.PowerShell7, "Fix")]
    public void AReleaseOlderThanTheWordsIsNotRunBecauseItWouldStartItsTray(ShellKind shell, string action)
    {
        using var world = new InstallerWorld();

        InstallerRun run = Run(world, shell, action, version: "1.4.0");

        Assert.AreEqual("Earshot: stopped. The latest release (1.4.0) has no " + action.ToLowerInvariant() + " command yet, so nothing was run.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("RunVerb"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ADownloadThatFailsItsChecksumIsNeverRun(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.ReplaceZip(world.Feed.Latest, [1, 2, 3, 4]);

        InstallerRun run = Run(world, shell, "Doctor");

        Assert.AreEqual("Earshot: stopped. The download did not match its checksum, so nothing was installed.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("RunVerb"));
        Assert.IsEmpty(run.CallsNamed("ExeVersion"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AWordThatEndsWithAFailureCodeIsReportedAsStopped(ShellKind shell)
    {
        using var world = new InstallerWorld();

        InstallerRun run = Run(world, shell, "Fix", exitCode: 74);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The fix command did not finish (");
        StringAssert.Contains(run.Final, "code 74");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AFixThePersonSaidNoToSaysNothingWasChanged(ShellKind shell)
    {
        using var world = new InstallerWorld();

        InstallerRun run = Run(world, shell, "Fix", exitCode: 76);

        Assert.AreEqual("Earshot: stopped. Fix connection stopped at your answer, so nothing was changed.", run.Final, run.Describe());
    }

    // The one real run of the helper that starts the program: a harmless Windows program stands in for Earshot.exe, so the
    // process start, the redirected output and the exit code are the script's own. findstr.exe finds nothing to match in no input and ends with 1.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheScriptStartsTheProgramForRealAndReadsItsExitCode(ShellKind shell)
    {
        string stand = Path.Combine(Environment.SystemDirectory, "findstr.exe");
        var zip = new ReleaseZipBuilder();
        zip.Files["Earshot.exe"] = File.ReadAllBytes(stand);
        using var world = new InstallerWorld(zip);

        InstallerRun run = Run(world, shell, "Doctor", realRun: true);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The doctor command did not finish (", run.Describe());
        StringAssert.Contains(run.Final, "code 1");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- Help -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void HelpPrintsTheGuideThatDocsAiGuideHoldsAndNothingElseButTheClosingLine(ShellKind shell)
    {
        using var world = new InstallerWorld();

        InstallerRun run = Run(world, shell, "Help");

        string printed = Normal(run.Out);
        Assert.IsTrue(printed.EndsWith("\nEarshot: done.", StringComparison.Ordinal), run.Describe());
        string guide = printed[..^"\nEarshot: done.".Length];
        Assert.AreEqual(Normal(File.ReadAllText(GuideFile())), guide);
        Assert.IsEmpty(run.Calls);
        Assert.IsEmpty(world.Feed.Requests, "Help asks no one.");
    }

    [TestMethod]
    public void TheGuideStatesTheFourLimitsTheExceptionAndWhatNeedsThePerson()
    {
        string file = File.ReadAllText(GuideFile());
        string guide = System.Text.RegularExpressions.Regex.Replace(file, @"\s+", " ");

        foreach (string needed in new[]
        {
            "Defender", "SmartScreen", "UAC", "Secure Boot", "other scripts from the internet",
            "Touch only the person's AirPods", "never the Bluetooth radio", "never other drivers",
            "remove everything", "Check every download's SHA-256 before it runs",
            "earshot.ps1 is itself run straight from the release", "If you run Setup.exe yourself, check it against the SHA-256 in the release notes first",
            "administrator prompts", "pairing mode", "Fix's two on-screen OK prompts",
            "without asking first", "https://github.com/5Muawiyah/earshot/issues", "Show the draft to the person",
            "Run Doctor first", "-Action Doctor", "Earshot.exe settings set NAME VALUE", "Earshot.exe doctor",
        })
        {
            StringAssert.Contains(guide, needed, StringComparison.OrdinalIgnoreCase);
        }

        Assert.IsFalse(file.Contains((char)0x2014, StringComparison.Ordinal) || file.Contains((char)0x2013, StringComparison.Ordinal), "No dashes beyond the hyphen.");
        Assert.IsTrue(file.All(c => c < 0x80), "Plain ASCII, so the script can carry it.");
    }

    // ----- the menu -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "5", "has no doctor command yet")]
    [DataRow(ShellKind.WindowsPowerShell, "6", "has no fix command yet")]
    [DataRow(ShellKind.WindowsPowerShell, "7", "# Earshot guide for AIs")]
    [DataRow(ShellKind.PowerShell7, "5", "has no doctor command yet")]
    [DataRow(ShellKind.PowerShell7, "6", "has no fix command yet")]
    [DataRow(ShellKind.PowerShell7, "7", "# Earshot guide for AIs")]
    public void TheMenuOffersDoctorFixAndHelp(ShellKind shell, string choice, string expected)
    {
        // The stub program in the feed's zip is not a real program, so it reads no version and Doctor and Fix stop plainly.
        using var world = new InstallerWorld();
        world.Spec.Action = null;
        world.Spec.CanAsk = true;
        world.Spec.Answers = [choice];

        InstallerRun run = world.Run(shell);

        foreach (string line in new[] { "  5  Doctor", "  6  Fix", "  7  Help", "Choose 1 to 7, or press Enter to cancel" })
        {
            CollectionAssert.Contains(run.Lines, line);
        }

        StringAssert.Contains(run.Out, expected);
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- the README's prompt -----

    [TestMethod]
    public void TheReadmePromptIsExactlyTheOneThatPointsAtTheGuide()
    {
        string readme = Normal(File.ReadAllText(ReadmeFile()));

        Assert.IsFalse(readme.Contains("### Install with AI", StringComparison.Ordinal), "The section was renamed.");
        int start = readme.IndexOf("### Install, fix or manage with AI", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "The renamed section is missing.");
        string section = readme[start..];
        int fenceStart = section.IndexOf("```\n", StringComparison.Ordinal) + 4;
        int fenceEnd = section.IndexOf("\n```", fenceStart, StringComparison.Ordinal);
        string block = section[fenceStart..fenceEnd];

        string expected = """
            Help me with Earshot, the AirPods app for Windows. I want to: <say it here,
            for example install it, fix my AirPods not connecting, or change a setting>.
            If you can run PowerShell on my PC, run exactly this first and follow the guide it prints:
            & ([scriptblock]::Create((irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1))) -Action Help
            If you can't run commands, read
            https://github.com/5Muawiyah/earshot/blob/main/docs/ai-guide.md
            and walk me through it one step at a time.
            """;
        Assert.AreEqual(Normal(expected), block);

        string afterBlock = section[(fenceEnd + 4)..];
        int next = afterBlock.IndexOf("\n### ", StringComparison.Ordinal);
        string between = next >= 0 ? afterBlock[..next] : afterBlock;
        Assert.AreEqual("", between.Trim(), "The section holds only the block.");
    }
}
