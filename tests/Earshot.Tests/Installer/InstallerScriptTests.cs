using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Earshot.Boot.Gate;
using Earshot.Tests.Phase4;
using Earshot.Tests.Update;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// installer\earshot.ps1, run for real in Windows PowerShell 5.1 and in PowerShell 7 against a local release feed that serves
// files as application/octet-stream, as GitHub does, and a stub install root. Only the boundary calls are replaced by
// hooks (the administrator prompt, the check of this PC, the running tray, starting the tray, the token check); the
// download, the checksum, the unzip, the quoting and every decision are the script's own. Every child runs with
// EARSHOT_SAFE_MODE and EARSHOT_DATA_ROOT set, and the script's own elevation refuses while either is set, so no test can
// reach a real install, update, repair or uninstall, and none ever asks Windows for administrator approval.
//
// Each row names its shell. A PowerShell 7 row is a skip on a PC without pwsh and runs on the hosted build.
[TestClass]
public sealed class InstallerScriptTests
{
    private const string UsageLine =
        "Usage: & ([scriptblock]::Create((irm https://github.com/5Muawiyah/earshot/releases/latest/download/earshot.ps1))) -Action Install|Update|Repair|Uninstall [-DryRun] [-RemoveSettings]";

    private const string AdministratorRefusalLine = "Earshot: stopped. Run this in a normal PowerShell window, not as administrator.";

    private static readonly string[] MenuLines = ["Earshot", "  1  Install", "  2  Update", "  3  Repair", "  4  Uninstall", "Choose 1 to 4, or press Enter to cancel"];
    private static readonly string[] ClosedThenElevated = ["ExitTray|4242", "Elevate"];

    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Address = "0A1B2C3D4E8C";
    private const string Container = "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13";

    // ----- what the file is -----

    [TestMethod]
    public void TheScriptIsPlainAsciiWithNoByteOrderMarkAndWindowsLineEndings()
    {
        byte[] bytes = File.ReadAllBytes(InstallerWorld.ScriptPath());

        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "A byte order mark.");
        int firstNonAscii = Array.FindIndex(bytes, b => b >= 0x80);
        Assert.AreEqual(-1, firstNonAscii, "Windows PowerShell 5.1 does not decode an octet-stream body as UTF-8, so a byte above 0x7F at offset " + firstNonAscii + " would be mangled before it ran.");
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                Assert.IsTrue(i > 0 && bytes[i - 1] == (byte)'\r', "A line feed with no carriage return at offset " + i + ".");
            }
        }

        string text = Encoding.ASCII.GetString(bytes);
        Assert.IsTrue(text.StartsWith("param([string]$Action, [switch]$DryRun, [switch]$RemoveSettings, [hashtable]$Roots, [string]$Feed, [hashtable]$TestHooks)", StringComparison.Ordinal),
            "The first line is the parameter block.");
        Assert.AreEqual(1, text.Split("\n& {", StringSplitOptions.None).Length - 1, "The body is one block.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheScriptParsesWithNoErrorsAndTheGuardsHold(ShellKind shell)
    {
        using var world = new InstallerWorld();
        string checker = world.WriteFile("check.ps1", AstChecker);

        InstallerRun run = world.RunRaw(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", checker, InstallerWorld.ScriptPath()]);

        Assert.AreEqual(0, run.ExitCode, run.Describe());
        Assert.AreEqual("0", run.Value("PARSEERRORS"), run.Describe());
        Assert.AreEqual("0", run.Value("EXITSTATEMENTS"), "The script must never exit: exit inside iex closes the person's window.");
        Assert.AreEqual("", run.Value("FORBIDDENCOMMANDS"), run.Describe());
        Assert.AreEqual("", run.Value("FORBIDDENTEXT"), run.Describe());
        Assert.AreEqual("", run.Value("OTHERURLS"), "Every address in the script is one of the two GitHub bases.");
        Assert.AreEqual("True", run.Value("HASONEPARAMBLOCK"), run.Describe());
    }

    private const string AstChecker = """
        param([string]$Path)
        $errors = $null
        $tokens = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
        'PARSEERRORS=' + @($errors).Count
        $exits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExitStatementAst] }, $true))
        'EXITSTATEMENTS=' + $exits.Count
        $forbidden = @('invoke-expression', 'iex', 'set-executionpolicy', 'unblock-file', 'add-mppreference', 'set-mppreference', 'start-bitstransfer', 'stop-process', 'invoke-webrequest', 'iwr', 'wget', 'curl', 'new-service', 'set-service', 'start-service', 'stop-service', 'reg', 'regedit', 'netsh', 'schtasks', 'sc')
        $names = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })
        'FORBIDDENCOMMANDS=' + ((@($names | Where-Object { $forbidden -contains $_.ToLowerInvariant() }) | Sort-Object -Unique) -join ',')
        # Prose in comments may name what the script never does; only code is checked.
        $text = (@($tokens | Where-Object { $_.Kind -ne 'Comment' } | ForEach-Object { $_.Text })) -join ' '
        $bad = @()
        foreach ($token in $tokens) {
            if ($token.Kind -eq 'Parameter' -and $token.Text -match '^-(ExecutionPolicy|Headers|UserAgent|Credential|Proxy)') { $bad += $token.Text }
        }
        foreach ($needle in @('Zone.Identifier', 'DefaultRequestHeaders', 'SkipCertificateCheck', 'ServerCertificateValidationCallback', 'Bypass', 'Unrestricted')) {
            if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $bad += $needle }
        }
        'FORBIDDENTEXT=' + ($bad -join ',')
        $urls = @([regex]::Matches($text, 'https?://[^\s''"`)]+') | ForEach-Object { $_.Value })
        $other = @($urls | Where-Object { $_ -notmatch '^https://(api\.)?github\.com/(repos/)?5Muawiyah/earshot/' })
        'OTHERURLS=' + ($other -join ',')
        'HASONEPARAMBLOCK=' + ($ast.ParamBlock -ne $null -and @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $false)).Count -eq 1)
        """;

    // ----- no one to ask -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void WithNoActionAndNoOneToAnswerItPrintsTheUsageLineAndMakesNoRequest(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = null;

        InstallerRun run = world.Run(shell);

        CollectionAssert.Contains(run.Lines, UsageLine);
        Assert.AreEqual("Earshot: stopped. No action was given and no one is here to ask.", run.Final);
        Assert.IsFalse(run.Lines.Any(l => l.Contains("Choose 1 to 4", StringComparison.Ordinal)), "No menu when no one can answer it.");
        Assert.IsEmpty(world.Feed.Requests, "Not one request before the usage line.");
        Assert.IsEmpty(run.Calls);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ANonInteractiveShellNeverShowsTheMenuEitherWayRedirectedOrNot(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = null;
        world.Spec.ExtraShellArguments = ["-NonInteractive"];

        InstallerRun run = world.Run(shell);

        CollectionAssert.Contains(run.Lines, UsageLine);
        Assert.IsFalse(run.Lines.Any(l => l.Contains("Choose 1 to 4", StringComparison.Ordinal)));
        Assert.IsEmpty(world.Feed.Requests);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void RunAsIrmPipedToIexItAsksNothingMakesNoFurtherRequestAndLeavesTheSessionAsItWas(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.ServeScript(File.ReadAllBytes(InstallerWorld.ScriptPath()));
        world.Spec.Raw = true;
        world.Spec.RawScript =
            "$eap = $ErrorActionPreference\r\n" +
            "$functions = (Get-ChildItem function:\\ | ForEach-Object Name | Sort-Object) -join ','\r\n" +
            "$variables = (Get-Variable | ForEach-Object Name | Sort-Object) -join ','\r\n" +
            "irm '" + world.Feed.FeedAddress + "earshot.ps1' | iex\r\n" +
            "'EAP-SAME=' + ($eap -eq $ErrorActionPreference)\r\n" +
            "'FUNCTIONS-SAME=' + ($functions -eq ((Get-ChildItem function:\\ | ForEach-Object Name | Sort-Object) -join ','))\r\n" +
            "$leaked = @((Get-Variable | ForEach-Object Name) | Where-Object { $variables.Split(',') -notcontains $_ -and @('Action','DryRun','RemoveSettings','Roots','Feed','TestHooks','st','hk','final','keepFolder') -contains $_ })\r\n" +
            "'LEAKED=' + ($leaked -join ',')\r\n";

        // The script reads the token of its own process, which is the test's. A hosted runner is an administrator, and there
        // the first thing the script says is the refusal, before it looks at any action.
        bool elevated = WindowsProcessToken.Current().IsElevatedAdministrator;

        InstallerRun run = world.Run(shell);

        if (elevated)
        {
            CollectionAssert.Contains(run.Lines, AdministratorRefusalLine, run.Describe());
            Assert.AreEqual(AdministratorRefusalLine, run.Lines.First(l => l.Length > 0), "The refusal is the script's first word.");
            CollectionAssert.DoesNotContain(run.Lines, UsageLine, "An administrator shell is refused before the action is looked at.");
        }
        else
        {
            CollectionAssert.Contains(run.Lines, UsageLine, run.Describe());
        }

        Assert.AreEqual("True", run.Value("EAP-SAME"), run.Describe());
        Assert.AreEqual("True", run.Value("FUNCTIONS-SAME"), run.Describe());
        Assert.AreEqual("", run.Value("LEAKED"), run.Describe());
        Assert.HasCount(1, world.Feed.Requests, "Only the script itself was fetched.");
        Assert.AreEqual("/earshot.ps1", world.Feed.Requests[0].Path);
    }

    // ----- the menu -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheMenuIsAskedOnlyWhereAnswersCanBeGivenAndEnterCancels(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = null;
        world.Spec.CanAsk = true;
        world.Spec.Answers = [""];

        InstallerRun run = world.Run(shell);

        int menu = Array.IndexOf(run.Lines, "Earshot");
        Assert.IsGreaterThanOrEqualTo(0, menu, run.Describe());
        CollectionAssert.AreEqual(
            MenuLines,
            run.Lines.Skip(menu).Take(6).ToArray());
        Assert.AreEqual("Earshot: stopped. Cancelled.", run.Final);
        Assert.IsEmpty(world.Feed.Requests);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ASeventhChoiceIsNotAChoiceAndNothingIsRequested(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = null;
        world.Spec.CanAsk = true;
        world.Spec.Answers = ["7"];

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. \"7\" is not one of the choices.", run.Final);
        Assert.IsEmpty(world.Feed.Requests);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ChoosingInstallFromTheMenuRunsInstallAndChoosingUninstallAsksAboutSettingsOnce(ShellKind shell)
    {
        using var install = new InstallerWorld();
        install.Spec.Action = null;
        install.Spec.CanAsk = true;
        install.Spec.Answers = ["1"];
        install.Spec.DryRun = true;
        InstallerRun first = install.Run(shell);
        Assert.IsTrue(first.Lines.Any(l => l.StartsWith("Would run as administrator:", StringComparison.Ordinal)), first.Describe());
        Assert.AreEqual("Earshot: done.", first.Final);

        using var uninstall = new InstallerWorld();
        uninstall.Spec.Action = null;
        uninstall.Spec.CanAsk = true;
        uninstall.Spec.Answers = ["4", "y"];
        Directory.CreateDirectory(uninstall.Roaming);
        Directory.CreateDirectory(uninstall.Local);
        InstallerRun second = uninstall.Run(shell);

        Assert.AreEqual(1, second.Lines.Count(l => l == "Remove your Earshot settings too? (y/N)"), second.Describe());
        Assert.IsFalse(Directory.Exists(uninstall.Roaming), "y removes the settings.");
        Assert.IsFalse(Directory.Exists(uninstall.Local));
        Assert.AreEqual("Earshot: done.", second.Final);
    }

    // ----- refusals before anything -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnAdministratorShellIsRefusedBeforeAnyRequest(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Elevated = true;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(AdministratorRefusalLine, run.Final);
        Assert.IsEmpty(world.Feed.Requests);
        Assert.IsEmpty(run.Calls);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AFeedThatIsNotHttpsAndNotLoopbackIsRefusedBeforeAnyRequest(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.FeedOverride = "http://192.0.2.1/";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. The feed address must be https.", run.Final);
        Assert.IsEmpty(world.Feed.Requests);
    }

    // ----- install -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void InstallOnAPcWithNothingResolvesOnceChecksThenElevatesTheVerifiedCopyWithTheExactVerbLine(ShellKind shell)
    {
        using var world = new InstallerWorld();

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.AreEqual(1, world.Feed.ApiRequests, "The release is resolved once.");
        Assert.AreEqual(1, world.Feed.ZipDownloads);
        Assert.AreEqual(1, world.Feed.ChecksumDownloads);
        Assert.AreEqual(3, world.Feed.Requests.Count, "Nothing else was asked of the feed.");
        CollectionAssert.Contains(run.Lines, "The download matches its checksum.");

        string[] elevate = run.CallsNamed("Elevate");
        Assert.HasCount(1, elevate, "One administrator prompt.");
        string[] parts = elevate[0].Split('|');
        string exe = parts[1];
        string line = string.Join('|', parts.Skip(2));
        StringAssert.EndsWith(exe, @"\app\Earshot\Earshot.exe");
        StringAssert.StartsWith(exe, world.TempRoot);
        string[] arguments = ArgvSplitter.Split(line);
        Assert.IsTrue(Program.TryParseInstallZipArgs(arguments, out UpdateRequest? request, out string? problem), problem + Environment.NewLine + line);
        Assert.AreEqual(world.Feed.Latest.ZipHash.ToUpperInvariant(), request.ZipSha256);
        Assert.AreEqual(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe))!)!, "update.zip"), request.ZipPath);
        Assert.AreEqual(Sid, request.Install.UserSid);
        Assert.AreEqual(Address, request.Install.Address);
        Assert.AreEqual(new Guid(Container), request.Install.ContainerId);
        CollectionAssert.AreEqual(new[] { "ZipSha|" + world.Feed.Latest.ZipHash.ToUpperInvariant() }, run.CallsNamed("ZipSha"), "At the prompt the zip the line names was there, and it is the zip whose hash is on the line.");

        CollectionAssert.AreEqual(new[] { world.InstalledExe }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray());
        Assert.IsTrue(run.IndexOfCall("Elevate") < run.IndexOfCall("StartTray"), "Earshot starts after the install.");
        Assert.IsFalse(Directory.EnumerateDirectories(world.TempRoot).Any(), "The download folder was removed.");
        Assert.IsFalse(run.Lines.Any(l => l.Contains('\r')), "Plain lines only.");
        world.AssertOnlyDefaultHeaders();
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ADryRunStopsBeforeElevationAndPrintsTheCommandItWouldRun(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.DryRun = true;
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string would = run.Lines.Single(l => l.StartsWith("Would run as administrator: ", StringComparison.Ordinal));
        StringAssert.Contains(would, "\\app\\Earshot\\Earshot.exe\" install-zip ");
        StringAssert.Contains(would, world.Feed.Latest.ZipHash.ToUpperInvariant());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsEmpty(run.CallsNamed("ExitTray"), "A dry run closes nothing.");
        Assert.IsEmpty(run.CallsNamed("StartTray"));
        CollectionAssert.Contains(run.Lines, "Would close Earshot first (process 4242).");
        Assert.AreEqual(1, world.Feed.ApiRequests, "It resolves and downloads and checks, as the real run does.");
        Assert.AreEqual(1, world.Feed.ZipDownloads);
    }

    // ----- anything that does not match stops before the unzip -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AZipThatDoesNotMatchItsChecksumStopsWithOnePlainLineBeforeAnyUnzip(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.ReplaceChecksum(world.Feed.Latest, Encoding.UTF8.GetBytes(new string('a', 64) + "  " + world.Feed.Latest.ZipName + "\n"));
        world.WatchTempRoot();

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. The download did not match its checksum, so nothing was installed.", run.Final, run.Describe());
        Assert.AreEqual(1, run.Lines.Count(l => l.Contains("did not match", StringComparison.Ordinal)), "One plain line.");
        Assert.IsFalse(world.CreatedDirectories().Any(d => d.EndsWith(@"\app", StringComparison.OrdinalIgnoreCase)), "No unzip: no app folder was ever made.");
        Assert.IsEmpty(run.CallsNamed("SetupValues"), "The unpacked copy was never run.");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsFalse(Directory.EnumerateDirectories(world.TempRoot).Any(), "The download that did not match was removed.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "no-hash")]
    [DataRow(ShellKind.WindowsPowerShell, "63-hex")]
    [DataRow(ShellKind.WindowsPowerShell, "another-file")]
    [DataRow(ShellKind.WindowsPowerShell, "two-lines")]
    [DataRow(ShellKind.WindowsPowerShell, "empty")]
    [DataRow(ShellKind.PowerShell7, "no-hash")]
    [DataRow(ShellKind.PowerShell7, "another-file")]
    public void AChecksumFileInTheWrongFormOrNamingAnotherFileStopsBeforeTheZipIsEvenFetched(ShellKind shell, string kind)
    {
        using var world = new InstallerWorld();
        FeedRelease latest = world.Feed.Latest;
        string good = latest.ZipHash.ToLowerInvariant();
        string body = kind switch
        {
            "no-hash" => latest.ZipName + "\n",
            "63-hex" => good[..63] + "  " + latest.ZipName + "\n",
            "another-file" => good + "  Earshot-9.9.9-win-x64.zip\n",
            "two-lines" => good + "  " + latest.ZipName + "\n" + good + "  " + latest.ZipName + "\n",
            _ => "",
        };
        world.Feed.ReplaceChecksum(latest, Encoding.UTF8.GetBytes(body));
        world.WatchTempRoot();

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. The release checksum file is not in the form this script reads, so nothing was installed.", run.Final, run.Describe());
        Assert.AreEqual(0, world.Feed.ZipDownloads, "A zip with no checksum to compare is not downloaded.");
        Assert.IsFalse(world.CreatedDirectories().Any(d => d.EndsWith(@"\app", StringComparison.OrdinalIgnoreCase)));
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, 404, "GitHub does not have the latest release (404).")]
    [DataRow(ShellKind.WindowsPowerShell, 403, "GitHub refused the request (403). Try again later.")]
    [DataRow(ShellKind.WindowsPowerShell, 429, "GitHub refused the request (429). Try again later.")]
    [DataRow(ShellKind.PowerShell7, 404, "GitHub does not have the latest release (404).")]
    [DataRow(ShellKind.PowerShell7, 403, "GitHub refused the request (403). Try again later.")]
    public void AnAnswerOfNotFoundOrRefusedFromTheReleaseFeedStopsWithOnePlainLine(ShellKind shell, int status, string expected)
    {
        using var world = new InstallerWorld();
        world.Feed.ApiAnswers((System.Net.HttpStatusCode)status);

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. " + expected, run.Final, run.Describe());
        Assert.AreEqual(0, world.Feed.ZipDownloads);
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AZipThatIsNotThereIsOnePlainLineAndNothingIsRunOrElevated(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.Remove(world.Feed.Latest);

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. GitHub does not have " + world.Feed.Latest.ZipName + " (404).", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("SetupValues"));
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AReleaseThatDoesNotListItsZipAndChecksumIsNotInstalled(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.PublishLatest(world.Feed.Latest, checksumAsset: false);

        InstallerRun run = world.Run(shell);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The release " + world.Feed.Latest.Tag + " does not list ");
        Assert.AreEqual(0, world.Feed.ZipDownloads);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ATagThatIsNotThreeNumbersIsNotOneThisScriptReads(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.PublishLatest(world.Feed.Latest, tagName: world.Feed.Latest.Tag + "-beta");

        InstallerRun run = world.Run(shell);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. The latest release has a tag this script does not read");
        Assert.AreEqual(0, world.Feed.ZipDownloads);
    }

    // ----- update, repair -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UpdateOfAnOlderUsableInstallGoesThroughTheInstalledProgramsOwnUpdateVerbAndWaitsForItsRecord(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; Write-Outcome 'Installed'; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string[] elevate = run.CallsNamed("Elevate");
        Assert.HasCount(1, elevate);
        string[] parts = elevate[0].Split('|');
        Assert.AreEqual(world.InstalledExe, parts[1], "The installed program's own verb, so it works against an install that has no new verbs.");
        string[] arguments = ArgvSplitter.Split(string.Join('|', parts.Skip(2)));
        Assert.IsTrue(Program.TryParseUpdateArgs(arguments, out UpdateRequest? request, out string? problem), problem);
        Assert.AreEqual(world.Feed.Latest.ZipHash.ToUpperInvariant(), request.ZipSha256);
        Assert.AreEqual(4242, request.TrayProcessId, "The process id of the tray that was closed.");
        Assert.AreEqual(Address, request.Install.Address);
        CollectionAssert.AreEqual(ClosedThenElevated, run.Calls.Where(c => c.StartsWith("ExitTray|", StringComparison.Ordinal) || c.StartsWith("Elevate|", StringComparison.Ordinal)).Select(c => c.StartsWith("Elevate", StringComparison.Ordinal) ? "Elevate" : c).ToArray(), "The tray is closed first.");
        CollectionAssert.AreEqual(new[] { world.InstalledExe }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray());
        Assert.AreEqual(1, world.Feed.ApiRequests);
    }

    // The install an update runs starts the tray itself when it has finished. A second start is told to show the running tray's
    // status card, which can cover the one-time notice of how the update went, so the script finds the tray the install started
    // and starts none of its own.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateWhoseInstallStartedTheTrayStartsNoSecondOne(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; Write-Outcome 'Installed'; $global:TrayRunning = $true; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.AreEqual(1, run.CallsNamed("ExitTray").Length, "The tray was closed for the update.");
        Assert.IsEmpty(run.CallsNamed("StartTray"), "The tray the install started is the one that runs: " + run.Describe());
        CollectionAssert.Contains(run.Lines, "Earshot is running.");
        Assert.IsFalse(run.Lines.Contains("Starting Earshot..."));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ARepairWhoseInstallStartedTheTrayStartsNoSecondOne(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Feed.AddRelease(BuildVersion.CurrentTag);
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Repair";
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "Write-Outcome 'Installed'; $global:TrayRunning = $true; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("StartTray"), run.Describe());
    }

    // The tray the install starts can appear a few seconds after the install says it has finished: it is waited for, not started
    // again at once.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateWhoseTrayAppearsAMomentAfterTheInstallEndedWaitsForItAndStartsNoSecondOne(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.TrayAppearSeconds = 8;
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; Write-Outcome 'Installed'; $global:TrayAppearAt = [DateTime]::UtcNow.AddSeconds(1.5); [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("StartTray"), run.Describe());
    }

    // When the install started none (it could not), the script starts the installed program once, after the wait.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateWhoseInstallStartedNoTrayStartsTheInstalledProgramOnceAfterTheWait(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.TrayAppearSeconds = 0.6;
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; Write-Outcome 'Installed'; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.AreEqual(new[] { world.InstalledExe }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray());
        CollectionAssert.Contains(run.Lines, "Starting Earshot...");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateTheInstalledProgramRefusesPrintsItsReasonAndStartsNothing(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.ElevateBody = "Write-Outcome 'Refused' 'the downloaded update did not match what was checked' 'update-verify-zip'; [pscustomobject]@{ ExitCode = 3 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. the downloaded update did not match what was checked. (update-verify-zip)", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("StartTray"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateWhoseInstallNeverSaysHowItEndedSaysSoAfterTheWaitingBudget(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Update";
        world.Spec.OutcomeWaitSeconds = 2;
        world.Spec.ElevateBody = "Write-Outcome 'Installing'; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. The update was still running when this script stopped waiting for it. Earshot will say how it went when it starts.", run.Final, run.Describe());
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnUpdateOfAnInstallThatIsAlreadyTheLatestSaysSoAndDownloadsNothing(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Feed.PublishLatest(world.Feed.AddRelease(BuildVersion.CurrentTag));
        world.Spec.Action = "Update";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, "Earshot " + BuildVersion.Current + " is up to date.");
        Assert.AreEqual(0, world.Feed.ZipDownloads);
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void InstallOverAnInstallThatIsAlreadyCurrentSaysItIsInstalledAndAnOlderOneIsTreatedAsAnUpdate(ShellKind shell)
    {
        using var current = new InstallerWorld();
        current.InstallRealProgram();
        current.Feed.PublishLatest(current.Feed.AddRelease(BuildVersion.CurrentTag));
        InstallerRun same = current.Run(shell);
        CollectionAssert.Contains(same.Lines, "Earshot " + BuildVersion.Current + " is already installed.");
        Assert.IsEmpty(same.CallsNamed("Elevate"));

        using var older = new InstallerWorld();
        older.InstallRealProgram();
        older.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        older.Spec.ElevateBody = "Write-Outcome 'Installed'; [pscustomobject]@{ ExitCode = 0 }";
        InstallerRun run = older.Run(shell);
        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        StringAssert.StartsWith(run.CallsNamed("Elevate").Single().Split('|')[2], "update ");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void RepairAsksForTheInstalledVersionsOwnReleaseNotTheLatestAndUsesTheUpdateVerb(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        FeedRelease installed = world.Feed.AddRelease(BuildVersion.CurrentTag);
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.Action = "Repair";
        world.Spec.ElevateBody = "Write-Outcome 'Installed'; [pscustomobject]@{ ExitCode = 0 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.AreEqual(0, world.Feed.ApiRequests, "A repair never asks which release is the latest.");
        Assert.IsTrue(world.Feed.Requests.All(r => r.Path.Contains("/" + BuildVersion.CurrentTag + "/", StringComparison.Ordinal)), "Only the installed version's files were fetched.");
        string[] parts = run.CallsNamed("Elevate").Single().Split('|');
        Assert.AreEqual(world.InstalledExe, parts[1]);
        Assert.IsTrue(Program.TryParseUpdateArgs(ArgvSplitter.Split(string.Join('|', parts.Skip(2))), out UpdateRequest? request, out string? problem), problem);
        Assert.AreEqual(installed.ZipHash.ToUpperInvariant(), request.ZipSha256);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void RepairOfAReleaseThatIsNoLongerOnGitHubSaysToChooseUpdate(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Repair";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. That release is no longer on GitHub. Choose Update instead.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- uninstall -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallRunsTheInstalledProgramsUninstallVerbOnceTheCheckedDownloadSaysItsFolderIsUsableAndKeepsTheSettings(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        Directory.CreateDirectory(world.Roaming);
        Directory.CreateDirectory(world.Local);
        File.WriteAllText(Path.Combine(world.Roaming, "settings.json"), "{}");
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string[] elevate = run.CallsNamed("Elevate");
        Assert.HasCount(1, elevate);
        Assert.AreEqual("Elevate|" + world.InstalledExe + "|uninstall", elevate[0]);
        Assert.AreEqual(1, world.Feed.ZipDownloads, "The installed program is not run as administrator until the checked download has looked at its folder.");
        Assert.AreEqual(1, run.CallsNamed("SetupValues").Length);
        CollectionAssert.Contains(run.Lines, "Your settings were kept.");
        Assert.IsTrue(File.Exists(Path.Combine(world.Roaming, "settings.json")));
        Assert.IsTrue(Directory.Exists(world.Local));
        Assert.IsEmpty(run.CallsNamed("StartTray"), "Earshot is not started after an uninstall.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallWithRemoveSettingsRemovesBothSettingsFoldersAndAnOpenOnStartupEntryThatNamesTheInstalledProgramOnly(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        Directory.CreateDirectory(world.Roaming);
        Directory.CreateDirectory(world.Local);
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.RemoveSettings = true;
        world.Spec.RunValue = "\"" + world.InstalledExe + "\" --startup";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsFalse(Directory.Exists(world.Roaming));
        Assert.IsFalse(Directory.Exists(world.Local));
        Assert.IsNull(world.ReadRunValue(), "The entry that started the installed program was removed.");
        CollectionAssert.Contains(run.Lines, "Your Earshot settings were removed.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnOpenOnStartupEntryThatNamesAnotherProgramIsLeftAlone(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.RunValue = "\"C:\\Tools\\Other\\Earshot.exe\" --startup";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.AreEqual("\"C:\\Tools\\Other\\Earshot.exe\" --startup", world.ReadRunValue());
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallWithNothingInstalledSaysSoAndStillMakesNoRequestOrPrompt(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = "Uninstall";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, "Earshot is not installed.");
        Assert.IsEmpty(world.Feed.Requests);
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallOfAnInstallWhoseProgramIsGoneUsesTheLatestReleasesVerifiedCopy(ShellKind shell)
    {
        using var world = new InstallerWorld();
        Directory.CreateDirectory(world.Install);
        world.Spec.Action = "Uninstall";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        string[] parts = run.CallsNamed("Elevate").Single().Split('|');
        StringAssert.EndsWith(parts[1], @"\app\Earshot\Earshot.exe");
        Assert.AreEqual("uninstall", parts[2]);
        Assert.AreEqual(1, world.Feed.ZipDownloads);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void UninstallWithDryRunPrintsTheCommandAndRunsNothing(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Action = "Uninstall";
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current);
        world.Spec.DryRun = true;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, "Would run as administrator: \"" + world.InstalledExe + "\" uninstall");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- declined, the tray, the elevation guard -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ADeclinedPromptChangesNothingSaysSoAndStartsTheTrayItClosed(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.PlaceProgramFile();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "throw (New-Object System.ComponentModel.Win32Exception 1223)";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Administrator approval was declined, so nothing was changed.", run.Final, run.Describe());
        CollectionAssert.AreEqual(new[] { "ExitTray|4242", "StartTray|" + world.InstalledExe }, run.Calls.Where(c => c.StartsWith("ExitTray", StringComparison.Ordinal) || c.StartsWith("StartTray", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(1, run.CallsNamed("Elevate").Length, "The prompt was shown once and not repeated.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ARunningTrayThatCannotBeAskedToExitWaitsForThePersonOrStopsWhenNoOneCanAnswer(ShellKind shell)
    {
        using var quiet = new InstallerWorld();
        quiet.Spec.Tray = new TrayStub(4242, quiet.InstalledExe, "1.2.1");
        quiet.Spec.CanAsk = false;
        InstallerRun refused = quiet.Run(shell);
        Assert.AreEqual("Earshot: stopped. Earshot is running. Choose Exit in its menu, then run this again.", refused.Final, refused.Describe());
        Assert.IsEmpty(refused.CallsNamed("Elevate"));
        Assert.IsEmpty(refused.CallsNamed("ExitTray"));

        using var asked = new InstallerWorld();
        asked.Spec.Tray = new TrayStub(4242, asked.InstalledExe, "1.2.1");
        asked.Spec.CanAsk = true;
        asked.Spec.Answers = [""];
        asked.Spec.TrayLeavesOnEnter = true;
        InstallerRun waited = asked.Run(shell);
        Assert.AreEqual("Earshot: done.", waited.Final, waited.Describe());
        CollectionAssert.Contains(waited.Lines, "Earshot is running. Choose Exit in its menu, then press Enter here.");
        Assert.IsEmpty(waited.CallsNamed("ExitTray"), "An older tray has no --exit to ask.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ATrayThatDoesNotCloseInTimeStopsTheRunWithNothingElevated(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.TrayStaysOpen = true;
        world.Spec.TrayWaitSeconds = 1;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Earshot did not close. Choose Exit in its menu and run this again.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsEmpty(run.CallsNamed("StartTray"), "It never closed, so it is still running and is not started a second time.");
    }

    // A tray that was asked to exit and was a moment slow: the script gives up on it, and by the time it stops the tray has
    // gone, so the tray it asked to exit is started again.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ATrayThatWasAskedToExitAndWasSlowIsStartedAgainWhenTheRunStopsOnItsAccount(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.PlaceProgramFile();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.TrayStaysOpen = true;
        world.Spec.TrayWaitSeconds = 0.0001;
        world.Spec.TrayFindsBeforeGone = 2;

        InstallerRun run = world.Run(shell);

        StringAssert.Contains(string.Join("\n", run.Lines), "Earshot: stopped. Earshot did not close.", run.Describe());
        CollectionAssert.AreEqual(new[] { world.InstalledExe }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray(), run.Describe());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // Ctrl+C ends the run without any stop being reported (PowerShell does not catch a pipeline stop), so the tray the run
    // closed is started again from the script's own finally, not its catch.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void CtrlCAtTheAdministratorPromptLeavesTheClosedTrayUnstartedAndSaysSo(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.PlaceProgramFile();
        world.Spec.Tray = new TrayStub(4242, world.InstalledExe, "1.3.0");
        world.Spec.ElevateBody = "throw [System.Management.Automation.PipelineStoppedException]::new()";

        InstallerRun run = world.Run(shell);

        CollectionAssert.AreEqual(ClosedThenElevated, run.Calls.Where(c => c.StartsWith("ExitTray|", StringComparison.Ordinal) || c.StartsWith("Elevate|", StringComparison.Ordinal)).Select(c => c.StartsWith("Elevate", StringComparison.Ordinal) ? "Elevate" : c).ToArray(), "The tray was closed, then the run was stopped at the administrator prompt.");
        Assert.IsEmpty(run.CallsNamed("StartTray"), "The prompt may still be approved, and the program it starts would be replacing files under a tray started now." + Environment.NewLine + run.Describe());
        Assert.IsFalse(run.Lines.Contains("Earshot was started again."), run.Describe());
        Assert.IsTrue(run.Lines.Any(l => l.StartsWith("Ctrl+C came while Windows was asking for administrator approval.", StringComparison.Ordinal)), run.Describe());
        Assert.IsFalse(run.Lines.Any(l => l.StartsWith("Earshot: done.", StringComparison.Ordinal)), "A run that was stopped by Ctrl+C is not a finished one.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void WithoutAHookTheScriptsOwnElevationRefusesWhileTheTestVariablesAreSetSoNoTestEverAsksForApproval(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.NoElevateHook = true;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Earshot's setup does not run while EARSHOT_SAFE_MODE is set.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    // ----- no AirPods paired, or several -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "not-paired", "Pair your AirPods with this PC in Bluetooth settings, then run this line again to finish installing. Until then Earshot cannot stop this PC paging them.")]
    [DataRow(ShellKind.PowerShell7, "not-paired", "Pair your AirPods with this PC in Bluetooth settings, then run this line again to finish installing. Until then Earshot cannot stop this PC paging them.")]
    public void WithNoAirPodsPairedYetTheVerifiedCopyIsPlacedForThisUserOnlyAndStartedWithNothingElevated(ShellKind shell, string reason, string message)
    {
        using var world = new InstallerWorld();
        world.Spec.Setup = InstallerWorld.SetupJson(ready: false, reason: reason);

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, message);
        CollectionAssert.Contains(run.Lines, "Already paired under a name without 'AirPods' in it? Choose them in Earshot's menu (Choose device), then run this line again.", "A pair that was renamed is not found by name, so the person is told how to point Earshot at it.");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsTrue(File.Exists(Path.Combine(world.UserPrograms, "Earshot.exe")), "The unpacked release was copied for this user.");
        CollectionAssert.AreEqual(new[] { Path.Combine(world.UserPrograms, "Earshot.exe") }, run.CallsNamed("StartTray").Select(c => c.Split('|')[1]).ToArray());
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnEarlierPerUserCopyIsReplacedOnlyWhenItIsOursAndAFolderThatIsNotIsLeftAlone(ShellKind shell)
    {
        using var ours = new InstallerWorld();
        Directory.CreateDirectory(ours.UserPrograms);
        File.WriteAllText(Path.Combine(ours.UserPrograms, "Earshot.files.json"), "{}");
        File.WriteAllText(Path.Combine(ours.UserPrograms, "stale.txt"), "old");
        ours.Spec.Setup = InstallerWorld.SetupJson(ready: false, reason: "not-paired");
        InstallerRun replaced = ours.Run(shell);
        Assert.AreEqual("Earshot: done.", replaced.Final, replaced.Describe());
        Assert.IsFalse(File.Exists(Path.Combine(ours.UserPrograms, "stale.txt")), "The earlier copy was replaced.");

        using var foreign = new InstallerWorld();
        Directory.CreateDirectory(foreign.UserPrograms);
        File.WriteAllText(Path.Combine(foreign.UserPrograms, "mine.txt"), "not Earshot");
        foreign.Spec.Setup = InstallerWorld.SetupJson(ready: false, reason: "not-paired");
        InstallerRun left = foreign.Run(shell);
        StringAssert.Contains(left.Final, "is there and is not an Earshot copy, so it was left alone.");
        Assert.IsTrue(File.Exists(Path.Combine(foreign.UserPrograms, "mine.txt")));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void WithNoDeviceAnInstallThatIsAlreadyThereIsNotTouched(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current, ready: false, reason: "not-paired");
        world.Spec.Action = "Update";

        InstallerRun run = world.Run(shell);

        StringAssert.StartsWith(run.Final, "Earshot: stopped. Earshot is installed, but no paired AirPods were found");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsFalse(Directory.Exists(world.UserPrograms));
    }

    // An install already there is told the list was not read in full, not that no AirPods were found.
    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell, "incomplete")]
    [DataRow(ShellKind.PowerShell7, "incomplete")]
    [DataRow(ShellKind.WindowsPowerShell, "unreadable")]
    public void WithAListNotReadInFullAnInstallThatIsAlreadyThereIsToldSoAndNotThatNoAirPodsWereFound(ShellKind shell, string reason)
    {
        using var world = new InstallerWorld();
        world.InstallRealProgram();
        world.Spec.Setup = InstallerWorld.SetupJson(state: "usable", version: BuildVersion.Current, ready: false, reason: reason);
        world.Spec.Action = "Update";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Earshot is installed, but Windows did not list all of your paired Bluetooth devices, so no AirPods could be found to set it up for. Check that Bluetooth is on, then run this again.", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("Elevate"));
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AFirstInstallAfterAPerUserCopyRemovesThatCopy(ShellKind shell)
    {
        using var world = new InstallerWorld();
        Directory.CreateDirectory(world.UserPrograms);
        File.WriteAllText(Path.Combine(world.UserPrograms, "Earshot.files.json"), "{}");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsFalse(Directory.Exists(world.UserPrograms));
    }

    // ----- output when redirected -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void WhenOutputIsRedirectedThereIsNoSpinnerFrameAndProgressIsPlainPercentLines(ShellKind shell)
    {
        using var world = new InstallerWorld();

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsFalse(run.Out.Replace("\r\n", "").Contains('\r'), "A carriage return that is not part of a line ending is a spinner frame.");
        foreach (string percent in new[] { "25%", "50%", "75%", "100%" })
        {
            Assert.IsTrue(run.Lines.Any(l => l.StartsWith("Downloading " + world.Feed.Latest.ZipName + ": " + percent, StringComparison.Ordinal)), "No line for " + percent + Environment.NewLine + run.Describe());
        }

        Assert.AreEqual(1, run.Lines.Count(l => l == "Installing..."), "One line for the wait, not a frame every 100 ms.");
        Assert.IsFalse(run.Out.Contains("\\ Installing", StringComparison.Ordinal) || run.Out.Contains("| Installing", StringComparison.Ordinal));
    }

    // ----- the line a tool is given -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheToolsLineFetchesTheScriptWithIrmTurnsItIntoAScriptBlockAndRunsItWithAnAction(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Feed.ServeScript(File.ReadAllBytes(InstallerWorld.ScriptPath()));
        world.Spec.FetchScript = true;
        world.Spec.DryRun = true;

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsTrue(run.Lines.Any(l => l.StartsWith("Would run as administrator: ", StringComparison.Ordinal)), run.Describe());
        Assert.AreEqual(1, world.Feed.Requests.Count(r => r.Path == "/earshot.ps1"), "The script was fetched once.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnActionThatIsNotOneOfTheFourIsRefusedBeforeAnyRequest(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = "Frobnicate";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. The action must be Install, Update, Repair or Uninstall, not Frobnicate.", run.Final, run.Describe());
        Assert.IsEmpty(world.Feed.Requests);
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ActionNamesAreMatchedWithoutRegardToCase(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.Action = "uninstall";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        CollectionAssert.Contains(run.Lines, "Earshot is not installed.");
    }

    // ----- a console: progress bar and spinner -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void OnAConsoleTheWaitForTheElevatedProgramShowsASpinnerAndThenTheResult(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.NotRedirected = true;
        // A real process that takes a couple of seconds and is not elevated: the script's own wait, its spinner and its
        // reading of the exit code all run for real. The prompt itself is never made.
        world.Spec.ElevateBody = "Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\cmd.exe') -ArgumentList '/c ping -n 3 127.0.0.1 >nul' -PassThru -WindowStyle Hidden";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: done.", run.Final, run.Describe());
        Assert.IsTrue(run.Out.Contains("\r| Installing...", StringComparison.Ordinal) || run.Out.Contains("\r/ Installing...", StringComparison.Ordinal) || run.Out.Contains("\r- Installing...", StringComparison.Ordinal) || run.Out.Contains("\r\\ Installing...", StringComparison.Ordinal),
            "No spinner frame." + Environment.NewLine + run.Describe());
        Assert.IsFalse(run.Lines.Any(l => l.StartsWith("Downloading ", StringComparison.Ordinal)), "The bar is drawn instead of percent lines.");
        Assert.IsFalse(run.Lines.Any(l => l == "Installing..."), "The single plain line is for redirected output.");
        Assert.IsEmpty(run.Err, "Nothing went wrong while the bar was drawn.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void AnElevatedProgramThatEndsWithANonZeroCodeStopsTheRunWithTheCodeAndTheRelease(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 21 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Setup did not finish (Windows did not give setup administrator rights, code 21).", run.Final, run.Describe());
        Assert.IsEmpty(run.CallsNamed("StartTray"), "Earshot is not started after a setup that did not finish.");
        Assert.IsTrue(run.Lines.Any(l => l.StartsWith("The downloaded files are in ", StringComparison.Ordinal)), "After a verified download that went wrong the folder is kept, and said.");
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void ExitCode26FromTheFirstInstallMeansAnInstallIsAlreadyThere(ShellKind shell)
    {
        using var world = new InstallerWorld();
        world.Spec.ElevateBody = "[pscustomobject]@{ ExitCode = 26 }";

        InstallerRun run = world.Run(shell);

        Assert.AreEqual("Earshot: stopped. Earshot is already installed. Use Update or Repair.", run.Final, run.Describe());
    }
    // ----- helpers run for real beside their C# counterparts -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheScriptsArgumentQuotingMatchesTheApplicationsOwnJoinArgumentsAndWindowsReadsItBack(ShellKind shell)
    {
        string[][] cases =
        [
            [@"update", @"C:\Users\A Person\AppData\Local\Temp\earshot-1a2b3c4d\update.zip", new string('A', 64)],
            [@"C:\plain\path.zip", "word", ""],
            ["with \"quote\" inside", @"trailing\", @"trailing slash and space\ "],
            [@"two\\slashes\\", "tab\there", "a\\\"b"],
            ["", "x y", @"C:\Program Files\Earshot\Earshot.exe"],
        ];
        using var world = new InstallerWorld();
        string json = JsonSerializer.Serialize(cases);
        string input = world.WriteFile("cases.json", json);
        world.Spec.Raw = true;
        world.Spec.RawScript =
            "$h = @{ Expose = { param($f) $cases = [IO.File]::ReadAllText(" + InstallerWorld.Q(input) + ") | ConvertFrom-Json\r\n" +
            "  foreach ($c in $cases) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((& $f.ConvertToArgumentLine ([string[]]@($c))))) } } }\r\n" +
            InstallerWorld.CallScript("-TestHooks $h");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(0, run.ExitCode, run.Describe());
        string[] built = run.Lines.Where(l => l.Length > 0).Select(l => Encoding.UTF8.GetString(Convert.FromBase64String(l))).ToArray();
        Assert.HasCount(cases.Length, built, run.Describe());
        for (int i = 0; i < cases.Length; i++)
        {
            Assert.AreEqual(UpdateHandover.JoinArguments(cases[i]), built[i], "Case " + i + ".");
            CollectionAssert.AreEqual(cases[i], ArgvSplitter.Split(built[i]), "Windows reads case " + i + " back as it was given.");
        }
    }

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheScriptsChecksumRuleAnswersExactlyAsTheApplicationsUpdaterDoesForEveryForm(ShellKind shell)
    {
        string zip = "Earshot-1.3.0-win-x64.zip";
        string hex = new string('a', 32) + new string('B', 32);
        var bodies = new List<byte[]>
        {
            Encoding.UTF8.GetBytes(hex + "  " + zip + "\n"),
            Encoding.UTF8.GetBytes(hex + "  " + zip),
            Encoding.UTF8.GetBytes(hex + "  " + zip + "\r\n"),
            Encoding.UTF8.GetBytes(hex + " *" + zip + "\n"),
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(hex + "  " + zip + "\n")).ToArray(),
            Encoding.UTF8.GetBytes(hex + "  " + zip + "\n\n"),
            Encoding.UTF8.GetBytes(hex + " " + zip + "\n"),
            Encoding.UTF8.GetBytes(hex + "\t" + zip + "\n"),
            Encoding.UTF8.GetBytes(hex[..63] + "  " + zip + "\n"),
            Encoding.UTF8.GetBytes(hex + "  " + zip + "x\n"),
            Encoding.UTF8.GetBytes(hex + "  earshot-1.3.0-win-x64.zip\n"),
            Encoding.UTF8.GetBytes(hex + "  " + zip + "\n" + hex + "  " + zip + "\n"),
            Encoding.UTF8.GetBytes(new string('g', 64) + "  " + zip + "\n"),
            Encoding.UTF8.GetBytes(""),
            new byte[] { 0xFF, 0xFE, 0x41 },
            Encoding.UTF8.GetBytes(zip + "\n"),
        };
        using var world = new InstallerWorld();
        string input = world.WriteFile("bodies.json", JsonSerializer.Serialize(bodies.Select(Convert.ToBase64String).ToArray()));
        world.Spec.Raw = true;
        world.Spec.RawScript =
            "$h = @{ Expose = { param($f) $all = [IO.File]::ReadAllText(" + InstallerWorld.Q(input) + ") | ConvertFrom-Json\r\n" +
            "  foreach ($b in $all) { $r = & $f.ReadChecksum ([Convert]::FromBase64String($b)) " + InstallerWorld.Q(zip) + "; if ($null -eq $r) { 'NONE' } else { $r } } } }\r\n" +
            InstallerWorld.CallScript("-TestHooks $h");

        InstallerRun run = world.Run(shell);

        Assert.AreEqual(0, run.ExitCode, run.Describe());
        string[] answers = run.Lines.Where(l => l.Length > 0).ToArray();
        Assert.HasCount(bodies.Count, answers, run.Describe());
        for (int i = 0; i < bodies.Count; i++)
        {
            bool expected = UpdateService.TryParseChecksum(bodies[i], zip, out string expectedHex);
            Assert.AreEqual(expected ? expectedHex : "NONE", answers[i], "Form " + i + ".");
        }
    }

    // ----- the real check of this PC, once -----

    [TestMethod]
    [DataRow(ShellKind.WindowsPowerShell)]
    [DataRow(ShellKind.PowerShell7)]
    public void TheRealProbeOfTheUnpackedProgramIsRunByTheScriptAndItsJsonIsReadWithNothingElevated(ShellKind shell)
    {
        string baseFolder = AppContext.BaseDirectory;
        string[] names = ["Earshot.exe", "Earshot.dll", "Earshot.deps.json", "Earshot.runtimeconfig.json", "Microsoft.Windows.SDK.NET.dll", "System.Speech.dll", "WinRT.Runtime.dll"];
        if (names.Any(n => !File.Exists(Path.Combine(baseFolder, n))))
        {
            // The hosted build says it must run every row; a test build with no program beside it must not pass there as a skip.
            if (Environment.GetEnvironmentVariable(PwshHost.RequireVariable) == "1")
            {
                Assert.Fail("The test build does not hold the program's files beside the tests, and " + PwshHost.RequireVariable + "=1 says this row must run.");
            }

            Assert.Inconclusive("The test build does not hold the program's files beside the tests.");
        }

        var builder = new ReleaseZipBuilder();
        builder.Files.Clear();
        foreach (string name in names)
        {
            builder.Files[name] = File.ReadAllBytes(Path.Combine(baseFolder, name));
        }

        using var world = new InstallerWorld(builder);
        world.Spec.DryRun = true;
        world.Spec.RealSetupValues = true;

        InstallerRun run = world.Run(shell);

        CollectionAssert.Contains(run.Lines, "Checking this PC...");
        Assert.IsEmpty(run.CallsNamed("Elevate"));
        Assert.IsFalse(run.Final.Contains("Something went wrong", StringComparison.Ordinal) || run.Final.Contains("could not be checked", StringComparison.Ordinal), "The real probe ran and its JSON was read." + Environment.NewLine + run.Describe());
        Assert.IsTrue(
            run.Lines.Any(l => l.StartsWith("Would run as administrator: ", StringComparison.Ordinal) || l.StartsWith("Would copy Earshot to ", StringComparison.Ordinal) || l.Contains("is up to date.", StringComparison.Ordinal) || l.Contains("already installed", StringComparison.Ordinal) || l.StartsWith("Earshot: stopped. Earshot is installed, but", StringComparison.Ordinal)),
            "The real probe's answer decided a route." + Environment.NewLine + run.Describe());
    }
}

// The tray the script should find running: its process id, its program and its version.
internal sealed record TrayStub(int Id, string Path, string Version);

internal sealed class RunSpec
{
    public string? Action { get; set; } = "Install";

    public bool DryRun { get; set; }

    public bool RemoveSettings { get; set; }

    public bool Elevated { get; set; }

    public bool? CanAsk { get; set; }

    public string[] Answers { get; set; } = [];

    public string Setup { get; set; } = InstallerWorld.SetupJson();

    public bool RealSetupValues { get; set; }

    public string ElevateBody { get; set; } = "[pscustomobject]@{ ExitCode = 0 }";

    public bool NoElevateHook { get; set; }

    public TrayStub? Tray { get; set; }

    public bool TrayStaysOpen { get; set; }

    public bool TrayLeavesOnEnter { get; set; }

    public double TrayWaitSeconds { get; set; } = 5;

    // How long the script looks for the tray an update's or a repair's install started before it starts one itself.
    public double TrayAppearSeconds { get; set; } = 0.3;

    // When set, the tray is found running this many times and is gone from the next look on, whatever asked it to exit: a
    // tray that was slow to close and was gone by the time the script looked again.
    public int? TrayFindsBeforeGone { get; set; }

    public int OutcomeWaitSeconds { get; set; } = 20;

    public string? FeedOverride { get; set; }

    public string? RunValue { get; set; }

    public string[] ExtraShellArguments { get; set; } = [];

    // Run the script as a tool does: fetched from the feed with irm and turned into a script block.
    public bool FetchScript { get; set; }

    // Make the script believe its output is a console: a progress bar and a spinner instead of plain lines.
    public bool NotRedirected { get; set; }

    // The whole driver is the given script, which calls the installer itself (see InstallerWorld.CallScript).
    public bool Raw { get; set; }

    public string RawScript { get; set; } = "";

    // When set, the driver runs in a runspace of its own and its pipeline is stopped, the way Ctrl+C stops it, a while after a
    // line starting with this text was recorded in the calls log: the stop lands wherever the script is then, and its finally
    // blocks run.
    public string? StopAfterCall { get; set; }

    public double StopDelaySeconds { get; set; } = 1.5;
}

internal sealed class InstallerRun
{
    public InstallerRun(int exitCode, string output, string errors, string[] calls)
    {
        ExitCode = exitCode;
        Out = output;
        Err = errors;
        Calls = calls;
        Lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    public int ExitCode { get; }

    public string Out { get; }

    public string Err { get; }

    public string[] Lines { get; }

    // What the hooks recorded, in order: "Elevate|<exe>|<line>", "ExitTray|<id>", "StartTray|<exe>", "SetupValues|<exe>".
    public string[] Calls { get; }

    public string Final => Lines.Last(l => l.Length > 0);

    public string[] CallsNamed(string name) => Calls.Where(c => c == name || c.StartsWith(name + "|", StringComparison.Ordinal)).ToArray();

    public int IndexOfCall(string name) => Array.FindIndex(Calls, c => c == name || c.StartsWith(name + "|", StringComparison.Ordinal));

    // The text after NAME= on a line the driver printed.
    public string Value(string name) => Lines.FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? "(no line)";

    public string Describe() =>
        "exit " + ExitCode + Environment.NewLine + "--- output" + Environment.NewLine + Out + Environment.NewLine + "--- errors" + Environment.NewLine + Err +
        Environment.NewLine + "--- calls" + Environment.NewLine + string.Join(Environment.NewLine, Calls);
}

// One test's folders, feed and way of running the script.
internal sealed class InstallerWorld : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly List<string> _created = new();
    private FileSystemWatcher? _watcher;

    public InstallerWorld(ReleaseZipBuilder? zip = null)
    {
        Feed = new InstallerFeed(zip: zip);
        Directory.CreateDirectory(TempRoot);
        Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
        Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
        Machine = _temp.File(Path.Combine("ProgramData", "Earshot"));
        UserPrograms = _temp.File(Path.Combine("LocalAppData", "Programs", "Earshot"));
        Roaming = _temp.File(Path.Combine("AppData", "Earshot"));
        Local = _temp.File(Path.Combine("LocalAppData", "Earshot"));
        RunKey = @"HKCU:\Software\EarshotInstallerTests\" + Guid.NewGuid().ToString("N");
        LogFile = _temp.File("calls.log");
    }

    public InstallerFeed Feed { get; }

    public RunSpec Spec { get; } = new();

    public string Install { get; }

    public string Machine { get; }

    public string UserPrograms { get; }

    public string Roaming { get; }

    public string Local { get; }

    public string RunKey { get; }

    public string LogFile { get; }

    public string TempRoot => _temp.File("Temp") + Path.DirectorySeparatorChar;

    public string InstalledExe => Path.Combine(Install, "Earshot.exe");

    public static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // installer\earshot.ps1 in the repository, found from the test build's folder.
    public static string ScriptPath()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "installer", "earshot.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("installer\\earshot.ps1 was not found above " + AppContext.BaseDirectory);
    }

    // What the check of this PC answers, with invented values.
    public static string SetupJson(string state = "nothing", string version = "", bool ready = true, string reason = "")
    {
        return JsonSerializer.Serialize(new
        {
            schema = 1,
            userSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
            ready,
            reason = ready ? "" : (reason.Length == 0 ? "not-paired" : reason),
            address = ready ? "0A1B2C3D4E8C" : "",
            containerId = ready ? "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13" : "",
            source = ready ? "paired" : "",
            install = new { state, problem = "", version },
        });
    }

    // The stub install root holds the test build's own program, so its file version is the real one (the script reads it).
    public void InstallRealProgram()
    {
        Directory.CreateDirectory(Install);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Earshot.exe"), InstalledExe);
    }

    // A file where the installed program would be, for a test whose tray ran from there. It is no program, so the script reads no version.
    public void PlaceProgramFile()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(InstalledExe, "a stand-in for the program");
    }

    public string WriteFile(string name, string content)
    {
        string path = _temp.File(name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void WatchTempRoot()
    {
        _watcher = new FileSystemWatcher(TempRoot.TrimEnd(Path.DirectorySeparatorChar)) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName };
        _watcher.Created += (_, e) =>
        {
            lock (_created)
            {
                _created.Add(e.FullPath);
            }
        };
        _watcher.EnableRaisingEvents = true;
    }

    public string[] CreatedDirectories()
    {
        Thread.Sleep(300);
        lock (_created)
        {
            return _created.ToArray();
        }
    }

    public string? ReadRunValue()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey["HKCU:\\".Length..]);
        return key?.GetValue("Earshot") as string;
    }

    // Every request the feed saw carried only the headers a shell sends by default, and none with a name the script chose.
    public void AssertOnlyDefaultHeaders()
    {
        foreach (FeedRequest request in Feed.Requests)
        {
            foreach (string name in request.HeaderNames)
            {
                Assert.IsTrue(IsDefaultHeader(name), "Unexpected header " + name + " on " + request.Path);
            }
        }

        FeedRequest api = Feed.Requests.First(r => r.Path == InstallerFeed.ApiPath);
        StringAssert.Contains(api.UserAgent, "PowerShell", "The address of the release is asked with the shell's own identification.");
    }

    // The standard request headers a shell or the framework's HTTP client sends on its own, named by the framework's own list.
    private static readonly HttpRequestHeader[] DefaultHeaders =
    [
        HttpRequestHeader.Host, HttpRequestHeader.UserAgent, HttpRequestHeader.Accept, HttpRequestHeader.AcceptEncoding,
        HttpRequestHeader.AcceptLanguage, HttpRequestHeader.Connection, HttpRequestHeader.CacheControl, HttpRequestHeader.Pragma, HttpRequestHeader.KeepAlive,
    ];

    private static bool IsDefaultHeader(string name) =>
        DefaultHeaders.Any(h => string.Equals(h.ToString(), name.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

    // The line that runs the script the way a tool does: & ([scriptblock]::Create(<the file>)) <arguments>.
    public static string CallScript(string arguments) =>
        "& ([scriptblock]::Create([IO.File]::ReadAllText(" + Q(ScriptPath()) + "))) " + arguments + "\r\n";

    // The line a tool is given: & ([scriptblock]::Create((irm <address of earshot.ps1>))) <arguments>.
    public string FetchAndCallScript(string arguments) =>
        "& ([scriptblock]::Create((irm " + Q(Feed.FeedAddress + "earshot.ps1") + "))) " + arguments + "\r\n";

    public InstallerRun Run(ShellKind shell)
    {
        string driver = Spec.Raw ? RawDriver() : BuildDriver();
        if (Spec.StopAfterCall is string stopAfter)
        {
            string inner = WriteFile("driver-inner.ps1", driver);
            driver =
                "$rs = [runspacefactory]::CreateRunspace($Host)\r\n" +
                "$rs.Open()\r\n" +
                "$ps = [powershell]::Create()\r\n" +
                "$ps.Runspace = $rs\r\n" +
                "[void]$ps.AddScript('& ' + " + Q(inner) + ")\r\n" +
                "$async = $ps.BeginInvoke()\r\n" +
                "$limit = [DateTime]::UtcNow.AddSeconds(90)\r\n" +
                "while (-not $async.IsCompleted -and [DateTime]::UtcNow -lt $limit) {\r\n" +
                "  if ((Test-Path -LiteralPath " + Q(LogFile) + ") -and (@(Get-Content -LiteralPath " + Q(LogFile) + ") | Where-Object { $_.StartsWith(" + Q(stopAfter) + ") }).Count -gt 0) { break }\r\n" +
                "  Start-Sleep -Milliseconds 100\r\n" +
                "}\r\n" +
                "Start-Sleep -Milliseconds " + (int)(Spec.StopDelaySeconds * 1000) + "\r\n" +
                "$ps.Stop()\r\n" +
                "$rs.Close()\r\n";
        }

        string path = WriteFile("driver.ps1", driver);
        var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass" };
        arguments.AddRange(Spec.ExtraShellArguments);
        arguments.AddRange(["-File", path]);
        return RunRaw(shell, arguments);
    }

    public InstallerRun RunRaw(ShellKind shell, IEnumerable<string> arguments)
    {
        string host = PwshHost.Require(shell);
        ProcessStartInfo info = PwshHost.CreateStartInfo(shell, host, arguments);
        info.RedirectStandardInput = true;
        info.Environment["EARSHOT_SAFE_MODE"] = "1";
        info.Environment["EARSHOT_DATA_ROOT"] = _temp.File("data");
        using Process process = Process.Start(info)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The script did not end within three minutes.");
        }

        process.WaitForExit();
        string[] calls = File.Exists(LogFile) ? File.ReadAllLines(LogFile).Where(l => l.Length > 0).ToArray() : [];
        return new InstallerRun(process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult(), calls);
    }

    private string RawDriver() => Spec.RawScript;

    private string BuildDriver()
    {
        var d = new StringBuilder();
        d.AppendLine("$ErrorActionPreference = 'Stop'");
        d.AppendLine("$global:NoteFile = " + Q(LogFile));
        d.AppendLine("function Note([string]$t) { Add-Content -LiteralPath $global:NoteFile -Value $t -Encoding ASCII }");
        d.AppendLine("function Write-Outcome([string]$Kind, [string]$Reason = '', [string]$Code = '') {");
        d.AppendLine("  [void](New-Item -ItemType Directory -Force -Path " + Q(Machine) + ")");
        d.AppendLine("  $o = [ordered]@{ SchemaVersion = 1; Id = ([Guid]::NewGuid().ToString('N')); WrittenUtc = [DateTime]::UtcNow.ToString('o'); Kind = $Kind; Version = ''; Reason = $Reason; Code = $Code }");
        d.AppendLine("  [IO.File]::WriteAllText(" + Q(Path.Combine(Machine, "update-outcome.json")) + ", ($o | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))");
        d.AppendLine("}");
        d.AppendLine("$global:Answers = @(" + string.Join(",", Spec.Answers.Select(Q)) + ")");
        d.AppendLine("$global:TrayRunning = " + (Spec.Tray is null ? "$false" : "$true"));
        d.AppendLine("$h = @{}");
        d.AppendLine("$h.IsElevated = { " + (Spec.Elevated ? "$true" : "$false") + " }");
        if (Spec.CanAsk is bool canAsk)
        {
            d.AppendLine("$h.CanAsk = " + (canAsk ? "$true" : "$false"));
        }

        d.AppendLine("$h.ReadLine = { if ($global:Answers.Count -eq 0) { return '' }; $a = $global:Answers[0]; $global:Answers = @($global:Answers | Select-Object -Skip 1); Note ('ReadLine|' + $a); " +
                     (Spec.TrayLeavesOnEnter ? "$global:TrayRunning = $false; " : "") + "return $a }");
        if (!Spec.RealSetupValues)
        {
            d.AppendLine("$h.SetupValues = { param($exe, $out) Note ('SetupValues|' + $exe); " + Q(Spec.Setup) + " }");
        }

        if (!Spec.NoElevateHook)
        {
            d.AppendLine("$h.Elevate = { param($exe, $line) Note ('Elevate|' + $exe + '|' + $line); $z = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $exe))) 'update.zip'; if (Test-Path -LiteralPath $z) { Note ('ZipSha|' + (Get-FileHash -LiteralPath $z -Algorithm SHA256).Hash) }; " + Spec.ElevateBody + " }");
        }

        if (Spec.Tray is { } tray)
        {
            string present = "if ($global:TrayRunning -or ($global:TrayAppearAt -and [DateTime]::UtcNow -ge $global:TrayAppearAt))";
            if (Spec.TrayFindsBeforeGone is int finds)
            {
                d.AppendLine("$global:FindTrayCalls = 0");
                present = "$global:FindTrayCalls = $global:FindTrayCalls + 1; if ($global:TrayRunning -and $global:FindTrayCalls -le " + finds + ")";
            }

            d.AppendLine("$h.FindTray = { " + present + " { @{ Id = " + tray.Id + "; Path = " + Q(tray.Path) + "; Version = " + Q(tray.Version) + " } } else { @() } }");
            d.AppendLine("$h.ExitTray = { param($t) Note ('ExitTray|' + $t.Id); " + (Spec.TrayStaysOpen ? "" : "$global:TrayRunning = $false") + " }");
        }
        else
        {
            d.AppendLine("$h.FindTray = { @() }");
            d.AppendLine("$h.ExitTray = { param($t) Note ('ExitTray|' + $t.Id) }");
        }

        d.AppendLine("$h.StartTray = { param($exe) Note ('StartTray|' + $exe) }");
        if (Spec.NotRedirected)
        {
            d.AppendLine("$h.Redirected = $false");
        }

        d.AppendLine("$h.TrayWaitSeconds = " + Spec.TrayWaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        d.AppendLine("$h.TrayAppearSeconds = " + Spec.TrayAppearSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        d.AppendLine("$h.OutcomeWaitSeconds = " + Spec.OutcomeWaitSeconds);

        d.AppendLine("$roots = @{ Install = " + Q(Install) + "; Machine = " + Q(Machine) + "; UserPrograms = " + Q(UserPrograms) + "; Roaming = " + Q(Roaming) +
                     "; Local = " + Q(Local) + "; Temp = " + Q(TempRoot) + "; RunKey = " + Q(RunKey) + " }");
        if (Spec.RunValue is not null)
        {
            d.AppendLine("[void](New-Item -Path " + Q(RunKey) + " -Force)");
            d.AppendLine("[void](New-ItemProperty -LiteralPath " + Q(RunKey) + " -Name 'Earshot' -Value " + Q(Spec.RunValue) + " -PropertyType String)");
        }

        var args = new StringBuilder();
        if (Spec.Action is not null)
        {
            args.Append("-Action ").Append(Spec.Action).Append(' ');
        }

        if (Spec.DryRun)
        {
            args.Append("-DryRun ");
        }

        if (Spec.RemoveSettings)
        {
            args.Append("-RemoveSettings ");
        }

        args.Append("-Feed ").Append(Q(Spec.FeedOverride ?? Feed.FeedAddress)).Append(" -Roots $roots -TestHooks $h");
        d.Append(Spec.FetchScript ? FetchAndCallScript(args.ToString()) : CallScript(args.ToString()));
        return d.ToString();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        Feed.Dispose();
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RunKey["HKCU:\\".Length..], throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // A key the test did not make is not touched; nothing was written under this name.
        }

        _temp.Dispose();
    }
}
