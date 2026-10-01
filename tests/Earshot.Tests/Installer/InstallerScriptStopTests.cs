using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// installer\earshot.ps1: what it does when a run stops, what a dry run leaves alone, and the small decisions it makes about the
// person's session and sign-in entry. Run for real in both shells against the same local release feed as InstallerScriptTests; a
// PowerShell 7 row is a skip on a PC without pwsh and runs on the hosted build.
[TestClass]
public sealed class InstallerScriptStopTests
{
    private static readonly string[] ClosedFirst = ["ExitTray|4242"];

    // ----- who can answer -----

    private sealed record AnswerCase(string[] Args, string Host, bool Redirected, bool Interactive, bool Expected);

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void WhetherAnyoneCanAnswerIsDecidedFromTheHostsOwnArgumentsAndFlagsInATable(ShellKind shell)
    {
        AnswerCase[] cases =
        [
            new([], "ConsoleHost", false, true, true),
            new(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass"], "ConsoleHost", false, true, true),
            new(["powershell.exe", "-NoLogo"], "ConsoleHost", false, true, true),
            new(["powershell.exe", "-File", "x.ps1"], "ConsoleHost", false, true, true),
            new(["powershell.exe", "-NonInteractive"], "ConsoleHost", false, true, false),
            new(["powershell.exe", "-noni"], "ConsoleHost", false, true, false),
            new(["powershell.exe", "-NONINTERACTIVE"], "ConsoleHost", false, true, false),
            new(["powershell.exe", "-nonin"], "ConsoleHost", false, true, false),
            new(["powershell.exe", "/noni"], "ConsoleHost", false, true, false),
            new(["powershell.exe", "-NoProfile", "-NonInteractive", "-File", "x.ps1"], "ConsoleHost", false, true, false),
            // Windows PowerShell 5.1 does not read -non as a switch (it is taken as a command), so it does not count.
            new(["powershell.exe", "-non"], "ConsoleHost", false, true, true),
            new(["powershell.exe", "-noninteractivex"], "ConsoleHost", false, true, true),
            new(["powershell.exe", "noni"], "ConsoleHost", false, true, true),
            new([], "Visual Studio Code Host", false, true, false),
            new([], "ServerRemoteHost", false, true, false),
            new([], "ConsoleHost", true, true, false),
            new([], "ConsoleHost", false, false, false),
            new(["powershell.exe", "-noni"], "ConsoleHost", true, true, false),
        ];
        using var world = new InstallerWorld();
        string input = world.WriteFile("answer-cases.json", JsonSerializer.Serialize(cases));
        world.Spec.Raw = true;
        world.Spec.RawScript =
            "$h = @{ Expose = { param($f) $cases = [IO.File]::ReadAllText(" + InstallerWorld.Q(input) + ") | ConvertFrom-Json\r\n" +
            "  foreach ($c in $cases) { $a = [string[]]@($c.Args); (& $f.TestCanAnswer $a $c.Host ([bool]$c.Redirected) ([bool]$c.Interactive)).ToString() } } }\r\n" +
            InstallerWorld.CallScript("-TestHooks $h");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(0, run.ExitCode, run.Describe());
        string[] answers = run.Lines.Where(l => l.Length > 0).ToArray();
        Assert.HasCount(cases.Length, answers, run.Describe());
        for (int i = 0; i < cases.Length; i++)
        {
            Assert.AreEqual(cases[i].Expected.ToString(), answers[i], "Case " + i + ": " + string.Join(' ', cases[i].Args) + " on " + cases[i].Host + ", redirected " + cases[i].Redirected + ", interactive " + cases[i].Interactive + ".");
        }
    }

    // ----- the sign-in entry -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ARunValueIsOursOnlyWhenItStartsExactlyOneOfOurProgramsInTheFormEarshotWritesIt(ShellKind shell)
    {
        const string installed = @"C:\Program Files\Earshot\Earshot.exe";
        const string perUser = @"C:\Users\A Person\AppData\Local\Programs\Earshot\Earshot.exe";
        (string Text, bool Expected)[] cases =
        [
            ("\"" + installed + "\" --startup", true),
            ("\"" + installed.ToUpperInvariant() + "\" --startup", true),
            ("\"" + perUser + "\" --startup", true),
            ("\"" + installed + "\"", false),
            ("\"" + installed + "\" --startup --other", false),
            (@"C:\Tools\wrapper.exe """ + installed + "\" --startup", false),
            ("\"" + installed + ".bak\" --startup", false),
            ("\"C:\\Program Files\\EarshotX\\Earshot.exe\" --startup", false),
            (installed + " --startup", false),
            ("", false),
        ];
        using var world = new InstallerWorld();
        string input = world.WriteFile("run-values.json", JsonSerializer.Serialize(cases.Select(c => c.Text).ToArray()));
        world.Spec.Raw = true;
        world.Spec.RawScript =
            "$h = @{ Expose = { param($f) $all = [IO.File]::ReadAllText(" + InstallerWorld.Q(input) + ") | ConvertFrom-Json\r\n" +
            "  foreach ($t in @($all)) { (& $f.TestOursRunValue ([string]$t) @(" + InstallerWorld.Q(installed) + ", " + InstallerWorld.Q(perUser) + ")).ToString() } } }\r\n" +
            InstallerWorld.CallScript("-TestHooks $h");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(0, run.ExitCode, run.Describe());
        string[] answers = run.Lines.Where(l => l.Length > 0).ToArray();
        Assert.HasCount(cases.Length, answers, run.Describe());
        for (int i = 0; i < cases.Length; i++)
        {
            Assert.AreEqual(cases[i].Expected.ToString(), answers[i], "Value " + i + ": " + cases[i].Text);
        }
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnOpenOnStartupEntryOfAnotherProgramThatMerelyNamesOurPathIsLeftAlone(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
        world.Spec.RunValue = @"C:\Tools\wrapper.exe """ + world.InstalledExe + "\" --startup";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.AreEqual(@"C:\Tools\wrapper.exe """ + world.InstalledExe + "\" --startup", world.ReadRunValue());
    }

    // ----- a dry run changes nothing -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, false)]
    [DataRow(ShellKind.WindowsPowerShell, true)]
    [DataRow(ShellKind.PowerShell7, false)]
    [DataRow(ShellKind.PowerShell7, true)]
    public void ADryRunUninstallWithNothingInstalledRemovesNeitherTheSettingsNorTheSignInEntryAndSaysWhatItWould(ShellKind shell, bool removeSettings)
    {
        using var world = new InstallerWorld();
        Directory.CreateDirectory(world.Roaming);
        Directory.CreateDirectory(world.Local);
        File.WriteAllText(Path.Combine(world.Roaming, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(world.Local, "log.txt"), "a log");
        string runValue = "\"" + world.InstalledExe + "\" --startup";
        world.Spec.Action = "Uninstall";
        world.Spec.DryRun = true;
        world.Spec.RemoveSettings = removeSettings;
        world.Spec.RunValue = runValue;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsTrue(File.Exists(Path.Combine(world.Roaming, "settings.json")), "The settings were not touched.");
        Assert.IsTrue(File.Exists(Path.Combine(world.Local, "log.txt")), "The log folder was not touched.");
        Assert.AreEqual(runValue, world.ReadRunValue(), "The sign-in entry was not touched.");
        CollectionAssert.Contains(run.Lines, "Would remove the Open on startup entry.");
        Assert.IsFalse(run.Lines.Any(l => l == "Open on startup was removed." || l == "Your Earshot settings were removed."), run.Describe());
        if (removeSettings)
        {
            CollectionAssert.Contains(run.Lines, "Would remove your Earshot settings (" + world.Roaming + " and " + world.Local + ").");
        }
        else
        {
            CollectionAssert.Contains(run.Lines, "Would keep your settings.");
        }
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ADryRunUninstallLeavesAPerUserCopyAndItsSettingsWhereTheyAre(ShellKind shell)
    {
        using var world = new InstallerWorld();
        Directory.CreateDirectory(world.UserPrograms);
        File.WriteAllText(Path.Combine(world.UserPrograms, "Earshot.files.json"), "{}");
        Directory.CreateDirectory(world.Local);
        world.Spec.Action = "Uninstall";
        world.Spec.DryRun = true;
        world.Spec.RemoveSettings = true;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsTrue(File.Exists(Path.Combine(world.UserPrograms, "Earshot.files.json")));
        Assert.IsTrue(Directory.Exists(world.Local));
        CollectionAssert.Contains(run.Lines, "Would close Earshot and remove " + world.UserPrograms + ". No administrator approval is needed.");
    }

    // ----- uninstall runs only a program whose folder has been checked -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallOfAnInstalledProgramWhoseFolderIsNotUsableRunsTheCheckedDownloadsProgramInstead(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "unusable", version: "1.2.1");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string[] parts = run.CallsNamed("Elevate").Single().Split('|');
        StringAssert.EndsWith(parts[1], @"\app\Earshot\Earshot.exe", "The installed program is never the one run as administrator when its folder did not pass.");
        Assert.AreEqual("uninstall", parts[2]);
        CollectionAssert.Contains(run.Lines, "The installed copy's folder is not one only administrators can change, so the downloaded copy removes it.");
        Assert.AreEqual(1, run.CallsNamed("SetupValues").Length);
    }

    // ----- several paired, or a list that could not be read -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "several", false, "Several AirPods are paired with this PC, so Earshot cannot tell which are yours. Remove the ones you do not use in Bluetooth settings, then run this again.")]
    [DataRow(ShellKind.WindowsPowerShell, "several", true, "Several AirPods are paired with this PC, so Earshot cannot tell which are yours. Remove the ones you do not use in Bluetooth settings, then run this again.")]
    [DataRow(ShellKind.WindowsPowerShell, "unreadable", false, "Windows did not list your paired Bluetooth devices. Check that Bluetooth is on, then run this again.")]
    [DataRow(ShellKind.WindowsPowerShell, "incomplete", false, "Windows listed only some of your paired Bluetooth devices, so Earshot cannot tell whether your AirPods are paired. Check that Bluetooth is on, then run this again.")]
    [DataRow(ShellKind.WindowsPowerShell, "incomplete", true, "Windows listed only some of your paired Bluetooth devices, so Earshot cannot tell whether your AirPods are paired. Check that Bluetooth is on, then run this again.")]
    [DataRow(ShellKind.PowerShell7, "incomplete", false, "Windows listed only some of your paired Bluetooth devices, so Earshot cannot tell whether your AirPods are paired. Check that Bluetooth is on, then run this again.")]
    [DataRow(ShellKind.WindowsPowerShell, "something-new", false, "Earshot could not tell which AirPods to set up for, so nothing was installed.")]
    [DataRow(ShellKind.PowerShell7, "several", false, "Several AirPods are paired with this PC, so Earshot cannot tell which are yours. Remove the ones you do not use in Bluetooth settings, then run this again.")]
    [DataRow(ShellKind.PowerShell7, "unreadable", false, "Windows did not list your paired Bluetooth devices. Check that Bluetooth is on, then run this again.")]
    public void WithSeveralPairedOrAListThatCouldNotBeReadNothingIsInstalledNotEvenForThisUserOnly(ShellKind shell, string reason, bool dryRun, string message)
    {
        using var world = new InstallerWorld();
        world.Spec.Setup = InstallerWorld.SetupJson(ready: false, reason: reason);
        world.Spec.DryRun = dryRun;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. " + message, run.Final, run.Describe());
        Assert.IsFalse(Directory.Exists(world.UserPrograms), "A copy that cannot keep the PC from paging paired AirPods is not offered as an install.");
        Assert.IsEmpty(run.CallsNamed("StartTray"));
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- a run that stops after it closed the tray starts the tray again -----

    // scenario, what the person is told, and whether the tray the script closed is started again.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "install-fails", "Earshot: stopped. Setup did not finish (a step of setup failed, code 3).", true)]
    [DataRow(ShellKind.WindowsPowerShell, "already-installed", "Earshot: stopped. Earshot is already installed. Use Update or Repair.", true)]
    [DataRow(ShellKind.WindowsPowerShell, "update-refused", "Earshot: stopped. the update could not be checked. (update-verify-zip)", true)]
    [DataRow(ShellKind.WindowsPowerShell, "uninstall-fails", "Earshot: stopped. Uninstall did not finish (a step of setup failed, code 3).", true)]
    [DataRow(ShellKind.WindowsPowerShell, "elevation-throws", "Earshot: stopped. Something went wrong: the prompt could not be shown", true)]
    [DataRow(ShellKind.WindowsPowerShell, "tray-program-gone", "Earshot: stopped. Setup did not finish (a step of setup failed, code 3).", false)]
    [DataRow(ShellKind.WindowsPowerShell, "install-still-running", "Earshot: stopped. Setup was still running when this script stopped waiting for it. It was left to finish. Run this line again in a few minutes to see where it got to.", false)]
    [DataRow(ShellKind.WindowsPowerShell, "update-still-running", "Earshot: stopped. The update was still running when this script stopped waiting for it. Earshot will say how it went when it starts.", false)]
    [DataRow(ShellKind.PowerShell7, "install-fails", "Earshot: stopped. Setup did not finish (a step of setup failed, code 3).", true)]
    [DataRow(ShellKind.PowerShell7, "update-refused", "Earshot: stopped. the update could not be checked. (update-verify-zip)", true)]
    [DataRow(ShellKind.PowerShell7, "uninstall-fails", "Earshot: stopped. Uninstall did not finish (a step of setup failed, code 3).", true)]
    [DataRow(ShellKind.PowerShell7, "install-still-running", "Earshot: stopped. Setup was still running when this script stopped waiting for it. It was left to finish. Run this line again in a few minutes to see where it got to.", false)]
    public void ARunThatStopsAfterItClosedTheTrayStartsItAgainUnlessSetupMayStillBeWorkingOrTheProgramIsGone(ShellKind shell, string scenario, string expectedFinal, bool started)
    {
        using var world = new InstallerWorld();
        string trayProgram = world.InstalledExe;
        switch (scenario)
        {
            case "install-fails":
                world.PlaceProgramFile();
                world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 3 }";
                break;
            case "already-installed":
                world.PlaceProgramFile();
                world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 26 }";
                break;
            case "update-refused":
                world.InstallRealProgram();
                world.Spec.Action = "Update";
                world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
                world.Spec.ElevateBody = "Write-Outcome 'Refused' 'the update could not be checked' 'update-verify-zip'; [pscustomobject]@{ ExitCode = 3 }";
                break;
            case "uninstall-fails":
                world.InstallRealProgram();
                world.Spec.Action = "Uninstall";
                world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
                world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 3 }";
                break;
            case "elevation-throws":
                world.PlaceProgramFile();
                world.Spec.ElevateBody = "throw 'the prompt could not be shown'";
                break;
            case "tray-program-gone":
                trayProgram = Path.Combine(world.Install, "Gone", "Earshot.exe");
                world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 3 }";
                break;
            case "install-still-running":
                world.PlaceProgramFile();
                world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 27 }";
                break;
            case "update-still-running":
                world.InstallRealProgram();
                world.Spec.Action = "Update";
                world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
                world.Spec.OutcomeWaitSeconds = 2;
                world.Spec.ElevateBody = "Write-Outcome 'Installing'; [pscustomobject]@{ ExitCode = 0 }";
                break;
            default:
                Assert.Fail("Unknown scenario " + scenario);
                break;
        }

        world.Spec.Tray = new TrayStub(4242, trayProgram, "1.3.0");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(expectedFinal, run.Final, run.Describe());
        CollectionAssert.AreEqual(ClosedFirst, run.CallsNamed("ExitTray"), "The tray was closed first.");
        if (started)
        {
            CollectionAssert.AreEqual(new[] { trayProgram }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray(), run.Describe());
            CollectionAssert.Contains(run.Lines, "Earshot was started again.");
        }
        else
        {
            Assert.IsEmpty(run.CallsNamed("StartTray"), run.Describe());
        }
    }

    // ----- Ctrl+C while the elevated program is working -----

    // A real process that takes a while and is not elevated stands in for the elevated program; its id is recorded so the test
    // can see it was still running when the run was stopped.
    private const string SlowStandIn =
        "$p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\cmd.exe') -ArgumentList '/c ping -n 20 127.0.0.1 >nul' -PassThru -WindowStyle Hidden; Note ('StandIn|' + $p.Id); $p";

    private static bool IsRunning(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void EndStandIn(InstallerRun run)
    {
        foreach (string call in run.CallsNamed("StandIn"))
        {
            try
            {
                using Process process = Process.GetProcessById(int.Parse(call.Split('|')[1], System.Globalization.CultureInfo.InvariantCulture));
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    // The closed tray is not started from a folder the elevated program is still replacing. The stop is made in the middle of
    // the script's own wait for a process that is really running, in both its forms (a spinner on a console, plain output).
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, false)]
    [DataRow(ShellKind.WindowsPowerShell, true)]
    [DataRow(ShellKind.PowerShell7, false)]
    [DataRow(ShellKind.PowerShell7, true)]
    public void CtrlCWhileTheElevatedProgramIsRunningDoesNotStartTheTrayAndSaysSetupIsStillRunning(ShellKind shell, bool onAConsole)
    {
        using var world = new InstallerWorld();
        world.PlaceProgramFile();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.NotRedirected = onAConsole;
        world.Spec.ElevateBody = SlowStandIn;
        world.Spec.StopAfterCall = "StandIn|";
        InstallerRun? run = null;
        try
        {
            run = world.Run(shell);

            CollectionAssert.AreEqual(ClosedFirst, run.CallsNamed("ExitTray"), run.Describe());
            int standIn = int.Parse(run.CallsNamed("StandIn").Single().Split('|')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(IsRunning(standIn), "The stop came while the elevated program was still working." + Environment.NewLine + run.Describe());
            Assert.IsEmpty(run.CallsNamed("StartTray"), "A program started now could be half replaced." + Environment.NewLine + run.Describe());
            // What a console shows of a line is what follows its last carriage return (the spinner is drawn over itself).
            Assert.IsTrue(run.Lines.Any(l => l[(l.LastIndexOf('\r') + 1)..].StartsWith("Setup is still running", StringComparison.Ordinal)), run.Describe());
            Assert.IsFalse(run.Lines.Any(l => l.StartsWith("Earshot: done.", StringComparison.Ordinal)), "A run that was stopped is not a finished one.");
            Assert.IsFalse(run.Lines.Contains("Earshot was started again."), run.Describe());
        }
        finally
        {
            if (run is not null)
            {
                EndStandIn(run);
            }
        }
    }

    // The same stop while the script waits for the update's record, after the elevated program handed over and exited.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void CtrlCWhileTheScriptWaitsForTheUpdatesRecordDoesNotStartTheTray(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Update";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
        world.Spec.OutcomeWaitSeconds = 60;
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        // A real process, as the elevated program is, that ends at once with 0 after it handed the update on.
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\cmd.exe') -ArgumentList '/c exit 0' -PassThru -WindowStyle Hidden";
        world.Spec.StopAfterCall = "Elevate|";
        world.Spec.StopDelaySeconds = 2;

        InstallerRun run = world.Run(shell);

        CollectionAssert.AreEqual(ClosedFirst, run.CallsNamed("ExitTray"), run.Describe());
        Assert.IsEmpty(run.CallsNamed("StartTray"), "The update may still be replacing files." + Environment.NewLine + run.Describe());
        Assert.IsTrue(run.Lines.Any(l => l.StartsWith("Setup is still running", StringComparison.Ordinal)), run.Describe());
    }

    // The hold ends when the exit code is read: a real elevated program that fails lets the tray start again.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ARealElevatedProgramThatEndsInFailureStillLetsTheClosedTrayStartAgain(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.PlaceProgramFile();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\cmd.exe') -ArgumentList '/c exit 3' -PassThru -WindowStyle Hidden";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Setup did not finish (a step of setup failed, code 3).", run.Final, run.Describe());
        CollectionAssert.AreEqual(new[] { world.InstalledExe }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray(), run.Describe());
    }

    // ----- the update's own record -----

    private static string StaleRecord(InstallerWorld world)
    {
        Directory.CreateDirectory(world.Machine);
        string file = Path.Combine(world.Machine, "update-outcome.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Id = "00000000000000000000000000000001",
            WrittenUtc = DateTime.UtcNow.AddDays(-3).ToString("o"),
            Kind = "Installed",
            Version = "1.2.0",
            Reason = "",
            Code = "",
        }));
        return file;
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnEarlierUpdatesRecordAlreadyThereIsNeverReadAsTheOutcomeOfThisUpdate(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        StaleRecord(world);
        world.Spec.Action = "Update";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
        world.Spec.OutcomeWaitSeconds = 2;
        world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The update was still running when this script stopped waiting for it.", "An old 'Installed' record is not this update's.");
        Assert.IsEmpty(run.CallsNamed("StartTray"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ANewRecordWithAnotherIdIsReadAsTheOutcomeEvenWhenAnEarlierOneIsThere(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        StaleRecord(world);
        world.Spec.Action = "Update";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
        world.Spec.ElevateBody = "Write-Outcome 'Installed'; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ARecordThatNeverReadsIsSaidSoWhenTheWaitRunsOut(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Update";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: "1.2.1");
        world.Spec.OutcomeWaitSeconds = 2;
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; [IO.File]::WriteAllText(" + InstallerWorld.Q(Path.Combine(world.Machine, "update-outcome.json")) + ", '{ not json'); [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The update was still running when this script stopped waiting for it.", run.Describe());
        StringAssert.Contains(run.Final, " The record of the update could not be read: ", "The reason the record never read is kept, not dropped.");
        Assert.IsGreaterThan("Earshot: stopped. The update was still running when this script stopped waiting for it. Earshot will say how it went when it starts. The record of the update could not be read: ".Length, run.Final.Length, "The text of the failure follows.");
    }

    // ----- the release lookup -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AReleaseLookupThatFailsWithNoAnswerAtAllKeepsTheTextOfTheFailure(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.FeedOverride = "http://127.0.0.1:1/";

        InstallerRun run = world.Run(shell);

        const string prefix = "Earshot: stopped. Could not reach GitHub for the latest release: ";
        StringAssert.StartsWith(run.Final, prefix, run.Describe());
        Assert.IsGreaterThan(prefix.Length + 5, run.Final.Length, "The framework's own words follow, so a person or a log can say why.");
    }
}
