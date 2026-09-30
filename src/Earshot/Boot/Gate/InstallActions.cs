using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Earshot.AudioProtection;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;

namespace Earshot.Boot.Gate;

// Creates and reads folder security. Behind an interface so install and the gate are tested against temp
// folders without an elevated token.
internal interface IFolderSecurity
{
    // Creates the folder with Sddl.MachineFolder (protected, owner Administrators). Like
    // FileSystemAclExtensions.Create it does nothing to a folder that already exists, so the caller always
    // reads the result back.
    StepOutcome CreateHardened(string path);

    // As CreateHardened, with the security descriptor the caller gives (the update's work folder has its own). Fails
    // when the folder already exists, so the caller never gets a folder someone else made first.
    StepOutcome CreateWithSddl(string path, string sddl);

    // The folder's owner, group and DACL as SDDL. Fails for a missing folder or a reparse point.
    StepOutcome ReadSddl(string path, out string? sddl);
}

// https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create
// https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.objectsecurity.getsecuritydescriptorsddlform
internal sealed class NtfsFolderSecurity : IFolderSecurity
{
    private const AccessControlSections Sections =
        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    public StepOutcome CreateHardened(string path) => Create(path, Sddl.MachineFolder, "machine-folder-create", failIfExists: false);

    public StepOutcome CreateWithSddl(string path, string sddl) => Create(path, sddl, "folder-create", failIfExists: true);

    private static StepOutcome Create(string path, string sddl, string step, bool failIfExists)
    {
        try
        {
            if (failIfExists && (Directory.Exists(path) || File.Exists(path)))
            {
                return StepOutcomes.NotAttempted(step, "Already exists, so it was not trusted: " + path);
            }

            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(sddl, Sections);
            new DirectoryInfo(path).Create(security);
            return new StepOutcome(step, true, 0, "S_OK", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // PrivilegeNotHeldException is an UnauthorizedAccessException; the rest are what the security
            // classes throw for a descriptor or a path they cannot use.
            // https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create
            return StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message);
        }
    }

    public StepOutcome ReadSddl(string path, out string? sddl)
    {
        sddl = null;
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return StepOutcomes.FromHResult("folder-acl-read", unchecked((int)0x80070003), "Missing: " + path);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return StepOutcomes.NotAttempted("folder-acl-read", "The folder is a reparse point: " + path);
            }

            sddl = info.GetAccessControl(Sections).GetSecurityDescriptorSddlForm(Sections);
            return new StepOutcome("folder-acl-read", true, 0, "S_OK", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // A folder whose security cannot be read is never trusted, whichever way the read failed.
            // https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.getaccesscontrol
            return StepOutcomes.FromHResult("folder-acl-read", ex.HResult, path + ": " + ex.Message);
        }
    }
}

// Task Scheduler writes for install and uninstall. Every method returns the HRESULT.
internal interface ITaskRegistrar
{
    int ReadFolderSddl(string folderPath, out string? sddl);

    int ListTasks(string folderPath, out IReadOnlyList<string> names);

    int DeleteTask(string folderPath, string name);

    int DeleteFolder(string parentPath, string name);

    int CreateFolder(string parentPath, string name, string sddl);

    // Builds the definition from the spec and registers it with the spec's principal and SDDL.
    int Register(string folderPath, TaskSpec spec, IList<StepOutcome> steps);

    int ReadTask(string taskPath, out string? sddl, out string? xml);
}

// The real registrar. Connects per call, on the calling thread (install runs it on a SystemWorker).
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-createfolder
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-registertaskdefinition
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletetask
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletefolder
internal sealed class ComTaskRegistrar : ITaskRegistrar
{
    public int ReadFolderSddl(string folderPath, out string? sddl)
    {
        string? read = null;
        int hr = ComTaskScheduler.WithFolder(folderPath, (_, folder) =>
            folder.GetSecurityDescriptor(ComTaskScheduler.SecurityInformation, out read));
        sddl = read;
        return hr;
    }

    public int ListTasks(string folderPath, out IReadOnlyList<string> names)
    {
        var found = new List<string>();
        int hr = ComTaskScheduler.WithFolder(folderPath, (_, folder) =>
        {
            int result = folder.GetTasks(TaskSchedulerCom.TASK_ENUM_HIDDEN, out IRegisteredTaskCollection? tasks);
            if (result < 0 || tasks is null)
            {
                return result < 0 ? result : ComActivation.E_POINTER;
            }

            try
            {
                result = tasks.get_Count(out int count);
                for (int i = 1; result >= 0 && i <= count; i++)
                {
                    result = tasks.get_Item(i, out IRegisteredTask? task);
                    if (result < 0 || task is null)
                    {
                        return result < 0 ? result : ComActivation.E_POINTER;
                    }

                    try
                    {
                        result = task.get_Name(out string? name);
                        if (result >= 0 && name is not null)
                        {
                            found.Add(name);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(task);
                    }
                }

                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(tasks);
            }
        });
        names = found;
        return hr;
    }

    public int DeleteTask(string folderPath, string name) =>
        ComTaskScheduler.WithFolder(folderPath, (_, folder) => folder.DeleteTask(name, 0));

    public int DeleteFolder(string parentPath, string name) =>
        ComTaskScheduler.WithFolder(parentPath, (_, folder) => folder.DeleteFolder(name, 0));

    public int CreateFolder(string parentPath, string name, string sddl) =>
        ComTaskScheduler.WithFolder(parentPath, (_, folder) =>
        {
            int result = folder.CreateFolder(name, sddl, out ITaskFolder? created);
            if (created is not null)
            {
                Marshal.ReleaseComObject(created);
            }

            return result;
        });

    public int Register(string folderPath, TaskSpec spec, IList<StepOutcome> steps) =>
        ComTaskScheduler.WithFolder(folderPath, (service, folder) =>
        {
            int result = service.NewTask(0, out ITaskDefinition? definition);
            if (result < 0 || definition is null)
            {
                return result < 0 ? result : ComActivation.E_POINTER;
            }

            try
            {
                result = TaskDefinitionWriter.Apply(definition, spec, steps);
                if (result < 0)
                {
                    return result;
                }

                // For TASK_LOGON_SERVICE_ACCOUNT the password must be VT_EMPTY (null).
                result = folder.RegisterTaskDefinition(
                    spec.Name, definition, TaskSchedulerCom.TASK_CREATE, spec.Principal.UserId, null,
                    spec.Principal.LogonType, spec.Sddl, out IRegisteredTask? registered);
                if (registered is not null)
                {
                    Marshal.ReleaseComObject(registered);
                }

                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(definition);
            }
        });

    public int ReadTask(string taskPath, out string? sddl, out string? xml)
    {
        int hr = ComTaskScheduler.ReadTask(taskPath, out TaskReadback? readback);
        sddl = readback?.Sddl;
        xml = readback?.Xml;
        return hr;
    }
}

// Who the process runs as.
internal interface IProcessToken
{
    string? UserSid { get; }

    bool IsLocalSystem { get; }

    // BUILTIN\Administrators is enabled in the token. Under UAC a filtered token holds it for deny only,
    // so this is false until the process is elevated. Local System holds it too.
    bool IsElevatedAdministrator { get; }
}

// https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsprincipal.isinrole
internal sealed class WindowsProcessToken : IProcessToken
{
    private WindowsProcessToken(string? userSid, bool isAdministrator)
    {
        UserSid = userSid;
        IsElevatedAdministrator = isAdministrator;
    }

    public string? UserSid { get; }

    public bool IsLocalSystem => string.Equals(UserSid, Sddl.LocalSystemSid, StringComparison.Ordinal);

    public bool IsElevatedAdministrator { get; }

    public static WindowsProcessToken Current()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return new WindowsProcessToken(identity.User?.Value, admin);
    }
}

// The SID an account name resolved to, or null with the failure in Step (its native code included).
internal sealed record AccountLookup(string? Sid, StepOutcome Step);

// Resolves an account name from task XML to its SID. A name that does not resolve is reported by the caller
// as a principal mismatch, next to this step. Translate throws IdentityNotMappedException for a name with
// no SID, Win32Exception (with the Win32 error) when the lookup itself fails, and ArgumentException for a
// name it cannot take; each keeps its code in the step.
// https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.ntaccount.translate
// https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.ntaccount.-ctor
internal static class AccountSids
{
    public const string Step = "account-lookup";

    public static AccountLookup Translate(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return new AccountLookup(null, StepOutcomes.NotAttempted(Step, "No account name to look up."));
        }

        string detail = "'" + account + "'";
        try
        {
            string sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
            return new AccountLookup(sid, StepOutcomes.FromHResult(Step, 0, detail));
        }
        catch (IdentityNotMappedException ex)
        {
            return new AccountLookup(null, StepOutcomes.FromHResult(Step, ex.HResult, detail + " has no SID: " + ex.Message, ok: false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new AccountLookup(null, StepOutcomes.FromWin32(Step, unchecked((uint)ex.NativeErrorCode), detail + ": " + ex.Message, ok: false));
        }
        catch (ArgumentException ex)
        {
            return new AccountLookup(null, StepOutcomes.FromHResult(Step, ex.HResult, detail + ": " + ex.Message, ok: false));
        }
        catch (InvalidOperationException ex)
        {
            return new AccountLookup(null, StepOutcomes.FromHResult(Step, ex.HResult, detail + ": " + ex.Message, ok: false));
        }
    }
}

internal sealed record InstallRequest(string UserSid, string Address, Guid ContainerId, TaskPrincipalMode Principal);

// SourceFolder: the running application folder. InstallFolder: %ProgramFiles%\Earshot. MachineFolder:
// %ProgramData%\Earshot. Tests pass temp folders.
internal sealed record InstallLayout(string SourceFolder, string InstallFolder, string MachineFolder);

internal sealed record InstallResult(GateExitCode Outcome, IReadOnlyList<StepOutcome> Steps);

// install <userSid> <addr12> <containerGuid> [--principal user], run elevated from the tray with one UAC
// prompt. The request is already validated. Each step is a StepOutcome and install stops at the first
// failure that would leave something unsafe (fail closed):
//   0. check the device with the same rule set-device applies (GateActions.ResolveAudioDevice): a
//      BTHENUM\DEV_<address> node whose container is the one given, with an A2DP sink node, so setup can never
//      pin a phone and the tray and the gate always name the same device. Then, when %ProgramData%\Earshot already
//      exists and passes its check, apply set-device's guards to the device.json in it (GateActions.CheckPinMove):
//      an uninstall that could not restore everything keeps device.json and protection.json as the only record of
//      what to allow and turn back on, and pinning another device over them would lose that record. Nothing is
//      changed before this passes, except the protection.json of a device removed from Windows, which is emptied;
//   1. read the publish manifest (Earshot.files.json) next to the running exe, copy exactly the files it lists,
//      and the manifest itself, to %ProgramFiles%\Earshot with IntegrityCopy (staged, then swapped in), check
//      each copy against the hash the manifest records, and check the install folder grants no one but
//      administrators write. Without a valid manifest nothing is copied: install runs from a release build. Run
//      from the install folder itself (a repair), nothing is copied, but the folder check, the installed
//      manifest and the hash of every file it lists must all pass;
//   2. create %ProgramData%\Earshot with the protected DACL, read it back and fail closed; a folder that
//      already existed and fails the check stops install and is left for the user to remove;
//   3. write device.json (the validated identity) and config.json (BlockAtBoot kept from a valid existing
//      config, otherwise the default: block on, hand back off);
//   4. delete any existing \Earshot task folder and its tasks, then create it with its SDDL and read it back;
//   5. register Gate, Protect and BootBlock, then read back each task's SDDL and XML and fail closed on any
//      difference, removing the tasks again;
//   6. register the hand-back service (ServicePlan), apply its pre-shutdown time-out and its access list, read
//      the registration back and fail closed on any difference, then start it, last of all, and wait for it to run.
//      The service is only the backstop for a shut down with the tray gone; the tasks are what block at every boot. So a
//      step of this stage that fails removes the half-registered service and nothing else: the verified tasks stay, the
//      result is Partial, and a step names the failed calls with their raw codes and says the backstop is not set up.
// A service left over from an earlier install is stopped first, before anything is copied, because it runs from the
// install folder that the copy moves aside: a folder with a running image in it is not assumed movable, so the stop is
// finished only when the service's process has exited too. A service that will not stop stops install with nothing
// replaced. If install then fails before the service is registered again, the service it stopped is started again, or
// the log says it stays stopped until the computer restarts.
// It never touches HKCU Run: the non-elevated tray owns its own startup value.
//
// A repair is this run from the installed copy (RepairOnly, which the repair verb sets): it is refused from any other
// folder before anything is checked, copies nothing, and otherwise does the same steps, every installed file against the
// installed manifest included.
internal sealed class InstallActions
{
    private readonly InstallLayout _layout;
    private readonly IFolderSecurity _folders;
    private readonly INodeReader _nodes;
    private readonly ITaskRegistrar _tasks;
    private readonly Func<string, AccountLookup> _accountToSid;
    private readonly ILog _log;
    private readonly IBluetoothServiceReader? _bluetooth;
    private readonly IServiceControl? _service;
    private bool _manifestMissing;

    // Set when this run stopped a running service before the copy, so a failure before the service is registered again
    // can start it again.
    private bool _stoppedRunningService;

    // How long install waits for the service to stop before it copies, and to run once it is started. Waiting budgets
    // chosen here; nothing measured how long either takes.
    public static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(30);

    // nodes is only read, to check the device before anything is changed. bluetooth is only read, to tell whether a
    // device pinned before has been removed from Windows; without it such a device's record is never taken as void.
    // service is the control manager for the hand-back service; the setup the tray starts always passes the real one,
    // and a run without one manages no service (the tests that are not about the service).
    public InstallActions(
        InstallLayout layout, IFolderSecurity folders, INodeReader nodes, ITaskRegistrar tasks, Func<string, AccountLookup> accountToSid, ILog log,
        IBluetoothServiceReader? bluetooth = null, IServiceControl? service = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(accountToSid);
        ArgumentNullException.ThrowIfNull(log);
        _service = service;
        _layout = layout;
        _folders = folders;
        _nodes = nodes;
        _tasks = tasks;
        _accountToSid = accountToSid;
        _log = log;
        _bluetooth = bluetooth;
    }

    internal IServiceControl? Service => _service;

    // The step a repair records in place of the copy. It is not a failure, and says so when a result is read for the
    // first thing that went wrong.
    internal const string NothingToCopyDetail = "Running from the install folder, so there is nothing to copy.";

    // A repair: install only ever runs from the installed copy and copies nothing. A run from any other folder is
    // refused before anything is checked or changed.
    internal bool RepairOnly { get; init; }

    // The machine-wide lock of setup, update and repair (InstallRunLock). The real runs set it; a run with none (the tests that
    // are not about it) takes no lock, since a named mutex shared by every test would make them wait on each other.
    internal IGateRunLock RunLock { get; init; } = NoGateRunLock.Instance;

    // How long the run waits for the lock before it refuses.
    internal TimeSpan LockWait { get; init; } = InstallRunLock.WaitBeforeRefusing;

    // For tests: the wait between polls of the service's state. Returning false stops waiting at once.
    internal Func<TimeSpan, bool> ServicePoll { get; init; } = DeviceChangeLock.SleepAndContinue;

    // The install folder is moved aside and the new one moved in. A program still running from the folder (the tray that
    // started an in-app update is still on its way out when setup begins) makes the move fail until it has let go, so a
    // failed move is tried again a few times before install gives up, each attempt a step with its raw code. Waiting
    // budgets chosen here: ten attempts half a second apart, about five seconds in all.
    public const int FolderMoveAttempts = 10;

    public static readonly TimeSpan FolderMoveDelay = TimeSpan.FromMilliseconds(500);

    // For tests: the wait between two attempts. Returning false stops trying at once.
    internal Func<TimeSpan, bool> FolderMoveWait { get; init; } = DeviceChangeLock.SleepAndContinue;

    // Nothing here is allowed to end the process without a record: an unexpected failure is logged and
    // returned with the steps taken so far, so a half-finished install is visible in the log and the exit code.
    public InstallResult Run(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = new List<StepOutcome>();
        IDisposable? held = null;
        try
        {
            // Before anything is read or changed: two of these never work on the folder, the tasks and the service at once.
            held = RunLock.TryEnter(LockWait, steps);
            if (held is null)
            {
                bool busy = steps.Any(InstallRunLock.IsBusy);
                _log.Warn("install: the machine-wide lock was not taken, so nothing was changed" + (busy ? " (another setup, update or repair holds it)." : "."));
                return new InstallResult(busy ? GateExitCode.Busy : GateExitCode.Failed, steps);
            }

            return RunSteps(request, steps);
        }
        catch (Exception ex)
        {
            steps.Add(ElevatedFailure.Step("install", ex));
            _log.Error("install stopped with " + ex.GetType().Name + " after " + steps.Count + " steps.", ex);
            return new InstallResult(GateExitCode.Failed, steps);
        }
        finally
        {
            held?.Dispose();
        }
    }

    private InstallResult RunSteps(InstallRequest request, List<StepOutcome> steps)
    {
        if (RepairOnly)
        {
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder));
            string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));
            if (!string.Equals(source, install, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(StepOutcomes.NotAttempted("repair-running-from",
                    "Repair runs only from the installed copy, " + install + ", and this program is in " + source + "."));
                return new InstallResult(GateExitCode.NotFromInstallFolder, steps);
            }

            steps.Add(new StepOutcome("repair-running-from", true, 0, "S_OK", install));
        }

        GateExitCode device = CheckDevice(request, steps);
        if (device != GateExitCode.Success)
        {
            return new InstallResult(device, steps);
        }

        GateExitCode kept = CheckKeptRecords(request, steps);
        if (kept != GateExitCode.Success)
        {
            return new InstallResult(kept, steps);
        }

        // A run from the install folder (a repair) copies nothing, so nothing it is about to do depends on the service being
        // stopped. The folder and every file in it are checked first: a repair that finds a file missing or different stops
        // here with the hand-back service still running and nothing changed.
        if (RunsFromInstallFolder() && !VerifyInstalledCopy(steps))
        {
            return new InstallResult(_manifestMissing ? GateExitCode.NoManifest : GateExitCode.Failed, steps);
        }

        if (!StopEarlierService(steps))
        {
            return new InstallResult(GateExitCode.Failed, steps);
        }

        if (!CopyApplication(steps))
        {
            RestartEarlierService(steps);
            return new InstallResult(_manifestMissing ? GateExitCode.NoManifest : GateExitCode.Failed, steps);
        }

        if (!PrepareMachineFolder(steps))
        {
            RestartEarlierService(steps);
            return new InstallResult(GateExitCode.FolderNotSecure, steps);
        }

        if (!WriteMachineFiles(request, steps))
        {
            RestartEarlierService(steps);
            return new InstallResult(GateExitCode.Failed, steps);
        }

        if (!RegisterTasks(request, steps))
        {
            return new InstallResult(GateExitCode.Failed, steps);
        }

        // The boot block is set up and verified by now. The service is the backstop for a shut down with the tray gone, so
        // when it cannot be set up the result says so and the tasks stay: Partial, never a setup that undoes the block.
        if (!RegisterService(steps))
        {
            return new InstallResult(GateExitCode.Partial, steps);
        }

        return new InstallResult(GateExitCode.Success, steps);
    }

    // The rule set-device applies, plus the container the tray passed: the address's device node must be in it. A
    // stale or mistaken container would leave the gate and the tray on different devices, so it is refused rather
    // than replaced with the node's.
    private GateExitCode CheckDevice(InstallRequest request, List<StepOutcome> steps)
    {
        GateExitCode resolved = GateActions.ResolveAudioDevice(_nodes, request.Address, "install-device", steps, out Guid container);
        if (resolved != GateExitCode.Success)
        {
            return resolved;
        }

        if (container != request.ContainerId)
        {
            steps.Add(StepOutcomes.NotAttempted("install-device",
                "The device node with address " + request.Address + " is in container " + container.ToString("D") +
                ", not " + request.ContainerId.ToString("D") + ", so nothing was pinned."));
            return GateExitCode.DeviceMismatch;
        }

        steps.Add(new StepOutcome("install-device", true, 0, "S_OK",
            request.Address + " in container " + container.ToString("D") + " has an A2DP sink node."));
        return GateExitCode.Success;
    }

    // The records an earlier install or a partial uninstall left in the machine folder. A folder that does not exist has
    // none; one that fails its check is never read (PrepareMachineFolder refuses it later), so nothing a standard user
    // may have put there can stop or steer install.
    private GateExitCode CheckKeptRecords(InstallRequest request, List<StepOutcome> steps)
    {
        string machine = _layout.MachineFolder;
        if (!Directory.Exists(machine))
        {
            return GateExitCode.Success;
        }

        // The check is repeated before the folder is used; its failure is recorded there, not twice.
        var trust = new List<StepOutcome>();
        if (!FolderTrust.IsTrusted(_folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", trust))
        {
            steps.Add(new StepOutcome("install-kept-records", true, NativeCodes.NotAttempted, NativeCodes.Name(NativeCodes.NotAttempted),
                machine + " did not pass its check, so no record in it was read."));
            return GateExitCode.Success;
        }

        steps.AddRange(trust);

        GateExitCode move = GateActions.CheckPinMove(_nodes, _bluetooth, new GateStore(machine), request.Address, request.ContainerId, "install-device", steps, _log);
        if (move is GateExitCode.OtherDeviceBlocked or GateExitCode.OtherDeviceProtected)
        {
            steps.Add(StepOutcomes.NotAttempted("install-device",
                "device.json names another device that Earshot still has to allow or turn back on. Set up for that device, or run uninstall again, first."));
        }

        return move;
    }

    // Whether this run is from the install folder itself: a repair, or an install verb run by an installed program that is
    // older than the repair verb.
    private bool RunsFromInstallFolder() =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder)),
            StringComparison.OrdinalIgnoreCase);

    // A repair run from the installed copy. The manifest rule holds here too: the folder must pass its check, hold the
    // manifest install copied into it, and every file it lists must still match its hash. Nothing is changed here, which is
    // why it runs before the hand-back service is stopped.
    private bool VerifyInstalledCopy(List<StepOutcome> steps)
    {
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));
        if (!CheckInstallFolder(install, steps))
        {
            return false;
        }

        FileManifest? installed = FileManifest.Read(install, out StepOutcome installedStep);
        steps.Add(installedStep);
        if (installed is null)
        {
            _manifestMissing = true;
            return false;
        }

        return VerifyInstalledFiles(install, installed, steps);
    }

    private bool CopyApplication(List<StepOutcome> steps)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder));
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));

        if (RunsFromInstallFolder())
        {
            // Already checked, before the service was stopped (VerifyInstalledCopy).
            steps.Add(StepOutcomes.NotAttempted("copy-app", NothingToCopyDetail));
            return true;
        }

        FileManifest? manifest = FileManifest.Read(source, out StepOutcome manifestStep);
        steps.Add(manifestStep);
        if (manifest is null)
        {
            _manifestMissing = true;
            return false;
        }

        string parent = Path.GetDirectoryName(install) ?? install;
        string suffix = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, Path.GetFileName(install) + ".staging-" + suffix);

        // The manifest goes with the files it lists, for a later repair run from the install folder.
        List<string> toCopy = manifest.Files.Select(f => f.RelativePath).Append(FileManifest.FileName).ToList();
        IntegrityCopyResult copy = IntegrityCopy.Copy(source, staging, toCopy);
        steps.AddRange(copy.Steps);
        if (!copy.Ok)
        {
            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        if (!MatchesManifest(manifest, copy, steps))
        {
            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        if (!copy.Files.Any(f => string.Equals(f.RelativePath, TaskPlan.ExecutableName, StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", TaskPlan.ExecutableName + " is not in the published file list."));
            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        string? old = null;
        if (Directory.Exists(install))
        {
            old = Path.Combine(parent, Path.GetFileName(install) + ".old-" + suffix);
            if (!FileSteps.MoveFolderWithRetry(install, old, "move-old-install", steps, FolderMoveAttempts, FolderMoveDelay, FolderMoveWait))
            {
                FileSteps.DeleteTree(staging, "remove-staging", steps);
                return false;
            }
        }

        if (!FileSteps.MoveFolderWithRetry(staging, install, "move-install", steps, FolderMoveAttempts, FolderMoveDelay, FolderMoveWait))
        {
            if (old is not null)
            {
                FileSteps.MoveFolder(old, install, "restore-old-install", steps);
            }

            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        // Leaving the old copy behind is not unsafe (it sits under Program Files), so neither a failed check nor
        // a failed delete stops install. A copy others could change is left for the user to remove.
        if (old is not null)
        {
            if (FolderTrust.IsTrusted(_folders, old, AclCheck.CheckInstallFolder, "old-install-folder-acl", steps))
            {
                FileSteps.DeleteTree(old, "remove-old-install", steps);
            }
            else
            {
                steps.Add(StepOutcomes.NotAttempted("remove-old-install", "Remove " + old + " by hand."));
            }
        }

        return CheckInstallFolder(install, steps);
    }

    // Every copied file matches the hash the manifest recorded when it was published, so a file changed
    // between publishing and installing is refused even though the copy itself was faithful. The copy of the
    // manifest itself must be the manifest that was read.
    private static bool MatchesManifest(FileManifest manifest, IntegrityCopyResult copy, List<StepOutcome> steps)
    {
        foreach (CopiedFile file in copy.Files)
        {
            string? expected = string.Equals(file.RelativePath, FileManifest.FileName, StringComparison.OrdinalIgnoreCase)
                ? manifest.ContentSha256
                : manifest.HashOf(file.RelativePath);
            if (expected is null || !string.Equals(expected, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(StepOutcomes.NotAttempted("verify-manifest:" + file.RelativePath,
                    expected is null
                        ? "The published file list does not hold this file."
                        : "The file does not match the hash recorded when it was published. " + FileManifest.MissingMessage));
                return false;
            }
        }

        steps.Add(new StepOutcome("verify-manifest", true, 0, "S_OK", copy.Files.Count + " files match the published hashes."));
        return true;
    }

    // A repair run: hashes each file the installed manifest lists, in place, stopping at the first that is not as
    // published. The folder has passed its check, so only administrators can change what is in it while this reads.
    // Earshot.exe must be listed.
    private static bool VerifyInstalledFiles(string install, FileManifest manifest, List<StepOutcome> steps) =>
        InstalledFileCheck.Verify(install, manifest, steps, stopAtFirst: true);

    private bool CheckInstallFolder(string install, List<StepOutcome> steps) =>
        FolderTrust.IsTrusted(_folders, install, AclCheck.CheckInstallFolder, "install-folder-acl", steps);

    // Any standard user can create %ProgramData%\Earshot first and owns what they create there, so a folder
    // that already exists and fails the check is never trusted and never deleted from this elevated process:
    // a recursive delete goes by path, and a subfolder swapped for a junction part way would send it outside
    // the folder. Install stops and asks for the folder to be removed by hand.
    private bool PrepareMachineFolder(List<StepOutcome> steps)
    {
        string machine = _layout.MachineFolder;
        if ((Directory.Exists(machine) || File.Exists(machine)) && !RemoveIfNotAPlainFolder(machine, steps))
        {
            return false;
        }

        if (!Directory.Exists(machine))
        {
            StepOutcome created = _folders.CreateHardened(machine);
            steps.Add(created);
            if (!created.Ok)
            {
                return false;
            }
        }

        if (FolderTrust.IsTrusted(_folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", steps))
        {
            return true;
        }

        steps.Add(StepOutcomes.NotAttempted("machine-folder-existing", "Remove " + machine + " by hand, then set up again."));
        return false;
    }

    // A file, or a junction or link, where the machine folder should be is removed (that one entry, never
    // anything it points to) so a real folder can be created. RemoveDirectoryW removes a junction whatever
    // its target holds, and fails on a real folder that is not empty.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-removedirectoryw
    private static bool RemoveIfNotAPlainFolder(string path, List<StepOutcome> steps)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                steps.Add(new StepOutcome("remove-machine-folder-file", true, 0, "S_OK", path));
                return true;
            }

            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete(recursive: false);
                steps.Add(new StepOutcome("remove-machine-folder-link", true, 0, "S_OK", path));
            }

            return true;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("remove-machine-folder-link", ex.HResult, path + ": " + ex.Message));
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("remove-machine-folder-link", ex.HResult, path + ": " + ex.Message));
            return false;
        }
    }

    private static bool WriteMachineFiles(InstallRequest request, List<StepOutcome> steps, GateStore store)
    {
        StepOutcome device = store.WriteDevice(new DeviceIdentity { Address = request.Address, ContainerId = request.ContainerId });
        steps.Add(device);
        if (!device.Ok)
        {
            return false;
        }

        GateRead<GateConfig> existing = store.ReadConfig();
        if (existing.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
        {
            steps.Add(existing.Step);
        }

        // Both settings are kept from a valid existing file, so setting up again never turns either back on or off.
        GateConfig kept = existing.IsOk && existing.Value is not null ? existing.Value : new GateConfig();
        StepOutcome config = store.WriteConfig(new GateConfig { BlockAtBoot = kept.BlockAtBoot, HandBackAtShutdown = kept.HandBackAtShutdown });
        steps.Add(config);
        return config.Ok;
    }

    private bool WriteMachineFiles(InstallRequest request, List<StepOutcome> steps) =>
        WriteMachineFiles(request, steps, new GateStore(_layout.MachineFolder));

    private bool RegisterTasks(InstallRequest request, List<StepOutcome> steps)
    {
        IReadOnlyList<TaskSpec> specs = TaskPlan.Build(_layout.InstallFolder, request.UserSid, request.Principal);

        if (!RemoveExistingFolder(steps))
        {
            return false;
        }

        int hr = _tasks.CreateFolder("\\", TaskPlan.FolderName, Sddl.TaskFolder(request.UserSid));
        steps.Add(StepOutcomes.FromHResult("task-folder-create", hr));
        if (hr < 0)
        {
            return false;
        }

        hr = _tasks.ReadFolderSddl(TaskPlan.FolderPath, out string? folderSddl);
        steps.Add(StepOutcomes.FromHResult("task-folder-read", hr));
        IReadOnlyList<string> problems = hr < 0 ? ["The task folder security could not be read."] : AclCheck.CheckTaskFolder(folderSddl, request.UserSid);
        if (!Accept("task-folder-acl", problems, steps))
        {
            RemoveTasksAndService(steps);
            return false;
        }

        foreach (TaskSpec spec in specs)
        {
            hr = _tasks.Register(TaskPlan.FolderPath, spec, steps);
            steps.Add(StepOutcomes.FromHResult("task-register:" + spec.Name, hr));
            if (hr < 0)
            {
                RemoveTasksAndService(steps);
                return false;
            }
        }

        foreach (TaskSpec spec in specs)
        {
            hr = _tasks.ReadTask(spec.Path, out string? sddl, out string? xml);
            steps.Add(StepOutcomes.FromHResult("task-read:" + spec.Name, hr));
            problems = hr < 0
                ? ["The task could not be read back."]
                : AclCheck.CheckTask(sddl, request.UserSid, spec.UserMayRun).Concat(TaskXmlCheck.Verify(xml, spec, _accountToSid, steps)).ToList();
            if (!Accept("task-verify:" + spec.Name, problems, steps))
            {
                RemoveTasksAndService(steps);
                return false;
            }
        }

        _log.Info("install: tasks registered for " + request.Principal + " principal.");
        return true;
    }

    // The root task folder lets Authenticated Users create folders, so an \Earshot folder may already hold
    // someone else's tasks. Everything in it is deleted and the folder removed; a folder that cannot be
    // removed (for example one with subfolders) stops install.
    private bool RemoveExistingFolder(List<StepOutcome> steps)
    {
        int hr = _tasks.ReadFolderSddl(TaskPlan.FolderPath, out _);
        if (hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
        {
            steps.Add(StepOutcomes.FromHResult("task-folder-existing", hr, "No earlier task folder.", ok: true));
            return true;
        }

        steps.Add(StepOutcomes.FromHResult("task-folder-existing", hr));
        if (hr < 0)
        {
            return false;
        }

        hr = _tasks.ListTasks(TaskPlan.FolderPath, out IReadOnlyList<string> names);
        steps.Add(StepOutcomes.FromHResult("task-folder-list", hr));
        if (hr < 0)
        {
            return false;
        }

        foreach (string name in names)
        {
            hr = _tasks.DeleteTask(TaskPlan.FolderPath, name);
            steps.Add(StepOutcomes.FromHResult("task-delete:" + name, hr));
            if (hr < 0)
            {
                return false;
            }
        }

        hr = _tasks.DeleteFolder("\\", TaskPlan.FolderName);
        steps.Add(StepOutcomes.FromHResult("task-folder-delete", hr, hr < 0 ? "Remove the \\Earshot task folder by hand, then set up again." : null));
        return hr >= 0;
    }

    // The service from an earlier install runs from the install folder that CopyApplication moves aside, so it is
    // stopped first. A service that is not there, or is stopped already, needs nothing. One that will not stop stops
    // install before anything is replaced.
    private bool StopEarlierService(List<StepOutcome> steps)
    {
        if (_service is null)
        {
            return true;
        }

        ServiceQuery read = _service.Query(ServicePlan.ServiceName);
        if (read.Presence == ServicePresence.Missing)
        {
            steps.Add(new StepOutcome(ServiceSteps.Existing, true, 0, "S_OK", "No earlier service."));
            return true;
        }

        if (read.Presence == ServicePresence.Unknown)
        {
            steps.AddRange(read.Steps.Where(s => !s.Ok));
            steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Stop,
                "Whether an earlier Earshot hand-back service is running could not be read, so nothing was replaced. Restart, then set up again."));
            return false;
        }

        steps.Add(new StepOutcome(ServiceSteps.Existing, true, 0, "S_OK", "An earlier service is " + ServiceSteps.StateName(read.State) + "."));
        if (read.State == AdvApi32.SERVICE_STOPPED)
        {
            return true;
        }

        if (ServiceStopper.StopAndWait(_service, ServicePlan.ServiceName, read, ServiceWait, ServicePoll, steps))
        {
            _stoppedRunningService = read.State == AdvApi32.SERVICE_RUNNING;
            return true;
        }

        steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Stop,
            "The Earshot hand-back service could not be stopped, so nothing was replaced. Restart, then set up again."));
        return false;
    }

    // Install failed after it had stopped the earlier service and before it registered the service again, so the service
    // is put back as it was: started. If that fails too, the record says so, so the owner knows there is no hand-back
    // service until the computer restarts.
    private void RestartEarlierService(List<StepOutcome> steps)
    {
        if (_service is null || !_stoppedRunningService)
        {
            return;
        }

        _stoppedRunningService = false;
        StepOutcome started = _service.Start(ServicePlan.ServiceName);
        steps.Add(started);
        if (started.Ok)
        {
            started = _service.WaitForState(ServicePlan.ServiceName, AdvApi32.SERVICE_RUNNING, ServiceWait, ServicePoll);
            steps.Add(started);
        }

        if (!started.Ok)
        {
            steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Start,
                "Setup stopped the Earshot hand-back service and could not start it again, so it stays stopped until the computer restarts."));
        }
    }

    // Registers the service, applies every value of its plan, reads the registration back, and only then starts it. This is
    // the backstop, not the main protection, so a failure here never touches the tasks: it removes what it registered
    // (a half-registered service is not left behind), records what failed with the raw codes, and says plainly that the
    // shut-down backstop is not set up. False in that case.
    private bool RegisterService(List<StepOutcome> steps)
    {
        if (_service is null)
        {
            return true;
        }

        int first = steps.Count;
        bool registered = false;
        if (TryRegisterService(steps, ref registered))
        {
            _log.Info("install: the hand-back service is registered and running.");
            return true;
        }

        if (registered)
        {
            ServiceRemoval.Remove(_service, ServicePoll, steps);
        }

        string codes = string.Join("; ", steps.Skip(first).Where(x => !x.Ok).Select(x => x.Step + " " + x.CodeName + " (" + x.Code + ")"));
        steps.Add(StepOutcomes.NotAttempted("service-backstop",
            "The Earshot hand-back service could not be set up" + (codes.Length == 0 ? "" : " (" + codes + ")") +
            ", so nothing hands the AirPods back at shut down when the tray is closed. The tasks are set up, so the AirPods are still blocked at every start. Set up again to try the service again."));
        _log.Warn("install: the hand-back service could not be set up" + (codes.Length == 0 ? "." : ": " + codes + "."));
        return false;
    }

    // registered: set once the control manager holds a registration this run created or changed, so the caller knows
    // there is something to remove.
    private bool TryRegisterService(List<StepOutcome> steps, ref bool registered)
    {
        ServiceSpec spec = ServicePlan.Spec(_layout.InstallFolder);
        ServiceQuery existing = _service!.Query(ServicePlan.ServiceName);
        if (existing.Presence == ServicePresence.Unknown)
        {
            steps.AddRange(existing.Steps.Where(s => !s.Ok));
            steps.Add(StepOutcomes.NotAttempted(ServiceSteps.Create, "Whether the Earshot hand-back service is registered could not be read."));
            return false;
        }

        // Present: brought to this plan in place. Absent: created. Either way every value comes from the plan.
        StepOutcome applied = existing.Presence == ServicePresence.Present ? _service.Reconfigure(spec) : _service.Create(spec);
        steps.Add(applied);
        if (!applied.Ok)
        {
            if (applied.Code == (int)AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE)
            {
                steps.Add(StepOutcomes.NotAttempted(applied.Step,
                    "The service from an earlier install is still marked for deletion. Restart, then set up again."));
            }

            return false;
        }

        registered = true;
        foreach (StepOutcome step in new[]
                 {
                     _service.SetDescription(spec.Name, spec.Description),
                     _service.SetPreshutdownTimeout(spec.Name, spec.PreshutdownTimeoutMs),
                 })
        {
            steps.Add(step);
            if (!step.Ok)
            {
                return false;
            }
        }

        if (!ApplyUnsetValues(spec, steps))
        {
            return false;
        }

        StepOutcome dacl = _service.SetDacl(spec.Name, spec.Sddl);
        steps.Add(dacl);
        if (!dacl.Ok)
        {
            return false;
        }

        ServiceQuery read = _service.Query(spec.Name);
        steps.AddRange(read.Steps.Where(s => !s.Ok));
        if (!Accept(ServiceSteps.Verify, ServiceCheck.Verify(read, spec), steps))
        {
            return false;
        }

        StepOutcome started = _service.Start(spec.Name);
        steps.Add(started);
        if (started.Ok)
        {
            StepOutcome running = _service.WaitForState(spec.Name, AdvApi32.SERVICE_RUNNING, ServiceWait, ServicePoll);
            steps.Add(running);
            started = running;
        }

        return started.Ok;
    }

    // What the plan leaves unset is set to that on a registration from an earlier install, so the registration is the
    // plan's whatever it was: no failure action, no delayed start, no service security identifier, no trigger. A fresh
    // service has none of them, so nothing is written for it. Only what differs is written, and a value that could not
    // be read is written too. Required privileges are not written (see ServicePlan); the read-back refuses them.
    private bool ApplyUnsetValues(ServiceSpec spec, List<StepOutcome> steps)
    {
        ServiceQuery current = _service!.Query(spec.Name);
        var changes = new List<StepOutcome>();
        if (current.FailureActionCount != 0 || !string.IsNullOrEmpty(current.FailureCommand))
        {
            changes.Add(_service.ClearFailureActions(spec.Name));
        }

        if (current.DelayedAutoStart != spec.DelayedAutoStart)
        {
            changes.Add(_service.SetDelayedAutoStart(spec.Name, spec.DelayedAutoStart));
        }

        if (current.ServiceSidType != spec.ServiceSidType)
        {
            changes.Add(_service.SetServiceSidType(spec.Name, spec.ServiceSidType));
        }

        // With none, the control manager refuses the call, so it is made only for a service that has some. A count that
        // could not be read is left to the read-back.
        if (current.TriggerCount is > 0)
        {
            changes.Add(_service.ClearTriggers(spec.Name));
        }

        steps.AddRange(changes);
        return changes.All(c => c.Ok);
    }

    // What a failure in the task stage does: the tasks and any service both go, so a half-finished task setup never stays
    // behind. The service stage does not use this: its failure leaves the verified tasks.
    private void RemoveTasksAndService(List<StepOutcome> steps)
    {
        RemoveTasks(steps);
        if (_service is not null)
        {
            ServiceRemoval.Remove(_service, ServicePoll, steps);
        }
    }

    private void RemoveTasks(List<StepOutcome> steps)
    {
        foreach (string name in TaskPlan.TaskNames)
        {
            int hr = _tasks.DeleteTask(TaskPlan.FolderPath, name);
            steps.Add(StepOutcomes.FromHResult("cleanup-task-delete:" + name, hr, ok: hr >= 0 || hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND));
        }

        int folderHr = _tasks.DeleteFolder("\\", TaskPlan.FolderName);
        steps.Add(StepOutcomes.FromHResult("cleanup-task-folder-delete", folderHr, ok: folderHr >= 0 || folderHr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND));
    }

    private static bool Accept(string step, IReadOnlyList<string> problems, List<StepOutcome> steps)
    {
        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted(step, problem));
        }

        return problems.Count == 0;
    }
}

// Reads a folder's security back and applies one of the AclCheck rules. Each problem becomes a step; a
// folder whose security cannot be read is not trusted.
internal static class FolderTrust
{
    public static bool IsTrusted(
        IFolderSecurity folders, string path, Func<string?, IReadOnlyList<string>> check, string step, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(steps);
        StepOutcome read = folders.ReadSddl(path, out string? sddl);
        steps.Add(read);
        if (!read.Ok)
        {
            return false;
        }

        IReadOnlyList<string> problems = check(sddl);
        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted(step, problem));
        }

        return problems.Count == 0;
    }
}

// File operations shared by install and uninstall, each recorded as a step.
internal static class FileSteps
{
    // Deletes a folder and everything in it. Directory.Delete removes a junction or link inside the tree
    // without following it, but it works by path, so it is only used on staging folders install created and
    // on folders whose security was read back and grants no one but administrators write (nobody else can
    // swap a subfolder part way). A folder that does not exist counts as deleted.
    // https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.delete
    public static bool DeleteTree(string path, string step, IList<StepOutcome> steps)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            var info = new DirectoryInfo(path);
            info.Delete(recursive: (info.Attributes & FileAttributes.ReparsePoint) == 0);
            steps.Add(new StepOutcome(step, true, 0, "S_OK", path));
            return true;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }

        return false;
    }

    // Moves a folder, trying again when the move fails, up to attempts tries with delay between them (wait returns false
    // to stop trying). Every failed try is a step with its raw code, and the move that worked is a step too.
    public static bool MoveFolderWithRetry(
        string from, string to, string step, IList<StepOutcome> steps, int attempts, TimeSpan delay, Func<TimeSpan, bool> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        for (int attempt = 1; ; attempt++)
        {
            int before = steps.Count;
            if (MoveFolder(from, to, step, steps))
            {
                return true;
            }

            if (attempt >= attempts || !wait(delay))
            {
                return false;
            }

            // The failed try is already the last step; say which one it was.
            StepOutcome failed = steps[before];
            steps[before] = failed with { Detail = "Attempt " + attempt + " of " + attempts + ": " + failed.Detail };
        }
    }

    public static bool MoveFolder(string from, string to, string step, IList<StepOutcome> steps)
    {
        try
        {
            Directory.Move(from, to);
            steps.Add(new StepOutcome(step, true, 0, "S_OK", from + " -> " + to));
            return true;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult(step, ex.HResult, from + " -> " + to + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult(step, ex.HResult, from + " -> " + to + ": " + ex.Message));
        }

        return false;
    }
}
