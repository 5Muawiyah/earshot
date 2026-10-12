using System.Globalization;

namespace Earshot.Setup;

internal enum SetupAction
{
    Install,
    Repair,
    FixConnection,
    Uninstall,
    Cancel,
}

internal enum MessageKind
{
    Information,
    Error,
}

// The screens. Behind an interface so the flow is tested with a scripted one and no window is ever shown by a test.
internal interface ISetupUi
{
    // One screen of command links; Cancel when the person closes it.
    SetupAction ChooseAction(string instruction, string content, string footer, IReadOnlyList<SetupAction> offered);

    // The index of the chosen name, or null when the person cancels.
    int? ChooseAirPods(IReadOnlyList<string> names);

    void Show(MessageKind kind, string instruction, string content);

    // Runs work while a "please wait" screen is up, and returns what it returned.
    T Work<T>(string instruction, string content, Func<T> work);
}

// Things about this PC that Setup reads, apart from the payload.
internal interface ISetupHost
{
    bool IsElevated { get; }

    // %ProgramData%\Earshot.
    string MachineFolder { get; }

    // %ProgramFiles%\Earshot\Earshot.exe.
    string InstalledExe { get; }

    // The SID of the account this process runs as, and of the person signed in to this session; null when unreadable.
    string? OwnUserSid { get; }

    string? SignedInUserSid { get; }

    // Why SignedInUserSid is null, with the raw code, or empty.
    string SignedInUserProblem { get; }
}

internal static class SetupExit
{
    public const int Done = 0;
    public const int Cancelled = 1;
    public const int PayloadDamaged = 2;
    public const int Failed = 3;
    public const int NotElevated = 4;
    public const int Downgrade = 5;
    public const int NoAirPods = 6;
    public const int NoStaging = 7;
    public const int NoPayload = 8;
    public const int NotThePersonSignedIn = 9;
}

internal sealed record SetupOutcome(int ExitCode, string Instruction, string Detail);

// The pure rules for which choices Setup offers and which it refuses.
internal static class SetupPlan
{
    public static IReadOnlyList<SetupAction> Offered(string installState) =>
        installState == SetupValuesReading.StateNothing
            ? [SetupAction.Install]
            : [SetupAction.Repair, SetupAction.FixConnection, SetupAction.Uninstall];

    // Why an action is refused, or null. Installing over a newer copy would be a downgrade, and Setup does not do that on its
    // own; Repair puts the installed copy right and may use an older download.
    public static string? Refusal(SetupAction action, Version? installed, Version payload)
    {
        if (action == SetupAction.Install && installed is not null && installed > payload)
        {
            return SetupText.Downgrade(Format(installed), Format(payload));
        }

        return null;
    }

    public static string Format(Version v) =>
        v.Major.ToString(CultureInfo.InvariantCulture) + "." + v.Minor.ToString(CultureInfo.InvariantCulture) + "." + v.Build.ToString(CultureInfo.InvariantCulture);

    // The exit codes of install-zip, repair and uninstall that are worth a sentence of their own (the install's own, 2 to 25,
    // and the two install-zip adds on top, 26 and 27).
    public static (string Instruction, string Content, bool Ok) Describe(SetupAction action, int exit)
    {
        switch (exit)
        {
            case 0:
                return action switch
                {
                    SetupAction.Repair => (SetupText.RepairedInstruction, SetupText.RepairedContent, true),
                    SetupAction.Uninstall => (SetupText.UninstalledInstruction, SetupText.UninstalledContent, true),
                    SetupAction.FixConnection => (SetupText.FixedInstruction, SetupText.FixedContent, true),
                    _ => (SetupText.InstalledInstruction, SetupText.InstalledContent, true),
                };
            case 2:
                return (SetupText.PartialInstruction, SetupText.PartialContent + " " + SetupText.Code(exit), false);
            case 25:
                return (SetupText.BusyInstruction, SetupText.BusyContent, false);
            case 26:
                return (SetupText.AlreadyInstalledInstruction, SetupText.AlreadyInstalledContent, false);
            case 27:
                return (SetupText.StillRunningInstruction, SetupText.StillRunningContent, false);
            default:
                return (SetupText.FailedInstruction, SetupText.FailedContent + " " + SetupText.Code(exit), false);
        }
    }
}

// Setup, from the first check to the last message. Every step that matters has one fixed place in the order:
//   1. the process is elevated, or nothing is done;
//   2. the embedded zip hashes to the value recorded at build, or nothing is unpacked;
//   3. a fresh folder is made under %ProgramData%\Earshot\setup-staging with a DACL only SYSTEM and Administrators are in,
//      and read back (StagingFolder);
//   4. the zip is written there as update.zip, hashed again, and unpacked beside it with the zip-slip guard, then every
//      file is checked against the release's own file list;
//   5. only now is any extracted program run, from that folder: "probe setup-values" says who is signed in, which AirPods are
//      paired and what is installed, and the person picks an action (and the AirPods, when there are several);
//   6. the action runs through the extracted program's own verbs, so Setup and earshot.ps1 share one registration:
//      install-zip (install, or repair of an install that cannot be used), repair (the installed program, with the
//      installed program's own files), uninstall, fix;
//   7. the working folder is removed.
// Nothing runs unelevated, and nothing the person can write is read before step 5.
internal sealed class SetupFlow
{
    internal static readonly TimeSpan ProbeWait = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ActionWait = TimeSpan.FromMinutes(12);

    // Earshot's own exit code for a Fix connection the person said no to on the screen (ExitCodes.Declined).
    internal const int FixDeclinedExit = 76;
    internal static readonly TimeSpan FixWait = TimeSpan.FromHours(1);

    private readonly IPayload _payload;
    private readonly IFolderAcl _acl;
    private readonly IProcessLauncher _launcher;
    private readonly ISetupUi _ui;
    private readonly ISetupHost _host;

    public SetupFlow(IPayload payload, IFolderAcl acl, IProcessLauncher launcher, ISetupUi ui, ISetupHost host)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(host);
        _payload = payload;
        _acl = acl;
        _launcher = launcher;
        _ui = ui;
        _host = host;
    }

    public SetupOutcome Run()
    {
        SetupOutcome outcome = RunSteps();
        if (outcome.ExitCode != SetupExit.Done && outcome.ExitCode != SetupExit.Cancelled)
        {
            _ui.Show(MessageKind.Error, outcome.Instruction, outcome.Detail);
        }

        return outcome;
    }

    private SetupOutcome RunSteps()
    {
        if (!_host.IsElevated)
        {
            return new SetupOutcome(SetupExit.NotElevated, SetupText.NotElevatedInstruction, SetupText.NotElevatedContent);
        }

        // Everything Setup registers (the tasks' user, the settings, Fix connection's records) is for the account it runs as.
        // When another administrator answered the prompt, that is not the person at the screen, so nothing is done.
        if (_host.OwnUserSid is not { } own || _host.SignedInUserSid is not { } person ||
            !string.Equals(own, person, StringComparison.OrdinalIgnoreCase))
        {
            return new SetupOutcome(SetupExit.NotThePersonSignedIn, SetupText.NotThePersonInstruction,
                (SetupText.NotThePersonContent + " " + _host.SignedInUserProblem).TrimEnd());
        }

        PayloadCheckResult check = PayloadCheck.Verify(_payload);
        if (check.Verdict == PayloadVerdict.NoPayload)
        {
            return new SetupOutcome(SetupExit.NoPayload, SetupText.NoPayloadInstruction, SetupText.NoPayloadContent);
        }

        Version? payloadVersion = PayloadCheck.ParseVersion(_payload.Version);
        if (check.Verdict == PayloadVerdict.Damaged || payloadVersion is null)
        {
            return new SetupOutcome(SetupExit.PayloadDamaged, SetupText.DamagedInstruction, SetupText.DamagedContent);
        }

        StagingResult staging = new StagingFolder(_acl, _host.MachineFolder).Create();
        if (!staging.Ok)
        {
            return new SetupOutcome(SetupExit.NoStaging, SetupText.StagingInstruction, staging.Detail);
        }

        string cleanup = "";
        try
        {
            return Stage(staging.Path, payloadVersion);
        }
        finally
        {
            cleanup = RemoveStaging(staging.Path);
            if (cleanup.Length > 0)
            {
                _ui.Show(MessageKind.Information, SetupText.CleanupNote, cleanup);
            }
        }
    }

    private SetupOutcome Stage(string staging, Version payloadVersion)
    {
        string zip = Path.Combine(staging, "update.zip");
        string app = Path.Combine(staging, "app");
        try
        {
            string written = WriteZip(zip);
            if (!string.Equals(written, _payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SetupOutcome(SetupExit.PayloadDamaged, SetupText.DamagedInstruction, SetupText.DamagedContent);
            }

            using (FileStream stream = File.OpenRead(zip))
            {
                ZipExtractor.Extract(stream, app);
            }

            ReleaseFileList.Verify(app);
        }
        catch (PayloadException ex)
        {
            return new SetupOutcome(SetupExit.Failed, SetupText.UnpackInstruction, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SetupOutcome(SetupExit.Failed, SetupText.UnpackInstruction, "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message);
        }

        string exe = Path.Combine(app, "Earshot.exe");
        string valuesFile = Path.Combine(staging, "setup-values.json");
        LaunchResult probe = _launcher.RunAndWait(exe, ["probe", "setup-values", "--out", valuesFile], app, ProbeWait);
        SetupValuesReading? values = probe.Started && File.Exists(valuesFile) ? SetupValuesReading.Parse(File.ReadAllText(valuesFile)) : null;
        if (values is null || !SetupValuesReading.IsUserSid(values.UserSid))
        {
            return new SetupOutcome(SetupExit.Failed, SetupText.ReadInstruction, SetupText.ReadContent + " " + probe.Detail);
        }

        Version? installed = PayloadCheck.ParseVersion(values.InstalledVersion);
        bool installedAlready = values.InstallState != SetupValuesReading.StateNothing;
        SetupAction action = _ui.ChooseAction(
            installedAlready ? SetupText.InstalledChoiceInstruction(values.InstalledVersion) : SetupText.InstallInstruction(_payload.Version),
            installedAlready ? SetupText.InstalledChoiceContent : SetupText.InstallContent(_payload.Version),
            SetupText.CheckFooter,
            SetupPlan.Offered(values.InstallState));
        if (action == SetupAction.Cancel)
        {
            return new SetupOutcome(SetupExit.Cancelled, "", "");
        }

        string? refusal = SetupPlan.Refusal(action, installed, payloadVersion);
        if (refusal is not null)
        {
            return new SetupOutcome(SetupExit.Downgrade, SetupText.DowngradeInstruction, refusal);
        }

        return action switch
        {
            SetupAction.Uninstall => Finish(action, RunWork(action, SetupText.UninstallingInstruction, exe, ["uninstall"], app, ActionWait)),
            SetupAction.FixConnection => Finish(action, RunWork(action, SetupText.FixingInstruction, exe, ["fix"], app, FixWait)),
            _ => InstallOrRepair(action, values, exe, zip, app),
        };
    }

    private SetupOutcome InstallOrRepair(SetupAction action, SetupValuesReading values, string exe, string zip, string app)
    {
        IReadOnlyList<AirPodsCandidate> choices = values.Choices();
        if (choices.Count == 0)
        {
            return new SetupOutcome(SetupExit.NoAirPods, SetupText.NoAirPodsInstruction, SetupText.NoAirPodsContent);
        }

        AirPodsCandidate chosen = choices[0];
        if (choices.Count > 1)
        {
            int? pick = _ui.ChooseAirPods(choices.Select((c, i) => SetupText.AirPodsChoice(c.Name, i)).ToList());
            if (pick is not { } index || index < 0 || index >= choices.Count)
            {
                return new SetupOutcome(SetupExit.Cancelled, "", "");
            }

            chosen = choices[index];
        }

        string container = SetupValuesReading.Hex(chosen.ContainerId);
        bool usable = values.InstallState == SetupValuesReading.StateUsable;
        string instruction = action == SetupAction.Repair ? SetupText.RepairingInstruction : SetupText.InstallingInstruction;

        // A usable install is repaired by its own program, which checks its own files against its own list. Anything else
        // (nothing there, or a copy that cannot be used) is installed from the checked zip, by the same verb earshot.ps1 uses.
        LaunchResult result = usable && action == SetupAction.Repair
            ? RunWork(action, instruction, _host.InstalledExe, ["repair", values.UserSid, chosen.Address, container], Path.GetDirectoryName(_host.InstalledExe)!, ActionWait)
            : RunWork(action, instruction, exe, ["install-zip", zip, _payload.Sha256.ToUpperInvariant(), values.UserSid, chosen.Address, container], app, ActionWait);
        return Finish(action, result);
    }

    private LaunchResult RunWork(SetupAction action, string instruction, string exe, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan wait) =>
        _ui.Work(instruction, SetupText.WorkContent, () => _launcher.RunAndWait(exe, arguments, workingDirectory, wait));

    // Shows the result of an action and turns it into the outcome. Setup starts nothing afterwards: it runs elevated, and the
    // only documented ways to start a program as the person signed in are not available to it, so the message says where to
    // open Earshot.
    private SetupOutcome Finish(SetupAction action, LaunchResult result)
    {
        if (!result.Started)
        {
            return new SetupOutcome(SetupExit.Failed, SetupText.FailedInstruction, SetupText.FailedContent + " " + result.Detail);
        }

        if (action == SetupAction.FixConnection && result.ExitCode == FixDeclinedExit)
        {
            _ui.Show(MessageKind.Information, SetupText.FixDeclinedInstruction, SetupText.NothingChanged);
            return new SetupOutcome(SetupExit.Cancelled, SetupText.FixDeclinedInstruction, SetupText.NothingChanged);
        }

        (string instruction, string content, bool ok) = SetupPlan.Describe(action, result.ExitCode);
        if (ok)
        {
            _ui.Show(MessageKind.Information, instruction, content);
            return new SetupOutcome(SetupExit.Done, instruction, content);
        }

        return new SetupOutcome(SetupExit.Failed, instruction, content);
    }

    // Copies the embedded zip into the staging folder through one handle and returns the SHA-256 of what was written. The
    // zip is then read from that copy, so nothing done to the original (the resource, or anything else) after this point
    // changes what is unpacked.
    private string WriteZip(string zip)
    {
        using Stream source = _payload.Open();
        using var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.None);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // The working folder is administrators-only, so a recursive delete by path cannot be steered by anyone else.
    private static string RemoveStaging(string staging)
    {
        try
        {
            Directory.Delete(staging, recursive: true);
            string parent = Path.GetDirectoryName(staging)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message;
        }
    }
}
