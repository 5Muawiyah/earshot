using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// Creates and reads folder security. Behind an interface so install and the gate are tested against temp
// folders without an elevated token.
internal interface IFolderSecurity
{
    // Creates the folder with Sddl.MachineFolder (protected, owner Administrators). Like
    // FileSystemAclExtensions.Create it does nothing to a folder that already exists, so the caller always
    // reads the result back.
    StepOutcome CreateHardened(string path);

    // The folder's owner, group and DACL as SDDL. Fails for a missing folder or a reparse point.
    StepOutcome ReadSddl(string path, out string? sddl);
}

// https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create
// https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.objectsecurity.getsecuritydescriptorsddlform
internal sealed class NtfsFolderSecurity : IFolderSecurity
{
    private const AccessControlSections Sections =
        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    public StepOutcome CreateHardened(string path)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(Sddl.MachineFolder, Sections);
            new DirectoryInfo(path).Create(security);
            return new StepOutcome("machine-folder-create", true, 0, "S_OK", path);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult("machine-folder-create", ex.HResult, path + ": " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult("machine-folder-create", ex.HResult, path + ": " + ex.Message);
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
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult("folder-acl-read", ex.HResult, path + ": " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
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

// Resolves an account name from task XML to its SID. Null when the name does not map; the caller reports
// that as a principal mismatch.
// https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.ntaccount.translate
internal static class AccountSids
{
    public static string? Translate(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        try
        {
            return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
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
//   1. copy the application to %ProgramFiles%\Earshot with IntegrityCopy (staged, then swapped in), and
//      check the install folder grants no one but administrators write;
//   2. create %ProgramData%\Earshot with the protected DACL, read it back and fail closed; a folder that
//      already existed and fails the check is removed and created again once;
//   3. write device.json (the validated identity) and config.json (BlockAtBoot kept from a valid existing
//      config, otherwise the default, on);
//   4. delete any existing \Earshot task folder and its tasks, then create it with its SDDL and read it back;
//   5. register Gate, Protect and BootBlock, then read back each task's SDDL and XML and fail closed on any
//      difference, removing the tasks again.
// It never touches HKCU Run: the non-elevated tray owns its own startup value.
internal sealed class InstallActions
{
    private readonly InstallLayout _layout;
    private readonly IFolderSecurity _folders;
    private readonly ITaskRegistrar _tasks;
    private readonly Func<string, string?> _accountToSid;
    private readonly ILog _log;

    public InstallActions(InstallLayout layout, IFolderSecurity folders, ITaskRegistrar tasks, Func<string, string?> accountToSid, ILog log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(accountToSid);
        ArgumentNullException.ThrowIfNull(log);
        _layout = layout;
        _folders = folders;
        _tasks = tasks;
        _accountToSid = accountToSid;
        _log = log;
    }

    public InstallResult Run(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = new List<StepOutcome>();

        if (!CopyApplication(steps))
        {
            return new InstallResult(GateExitCode.Failed, steps);
        }

        if (!PrepareMachineFolder(steps))
        {
            return new InstallResult(GateExitCode.FolderNotSecure, steps);
        }

        if (!WriteMachineFiles(request, steps))
        {
            return new InstallResult(GateExitCode.Failed, steps);
        }

        if (!RegisterTasks(request, steps))
        {
            return new InstallResult(GateExitCode.Failed, steps);
        }

        return new InstallResult(GateExitCode.Success, steps);
    }

    private bool CopyApplication(List<StepOutcome> steps)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder));
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));

        if (string.Equals(source, install, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", "Running from the install folder, so there is nothing to copy."));
            return CheckInstallFolder(install, steps);
        }

        string parent = Path.GetDirectoryName(install) ?? install;
        string suffix = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, Path.GetFileName(install) + ".staging-" + suffix);

        IntegrityCopyResult copy = IntegrityCopy.Copy(source, staging);
        steps.AddRange(copy.Steps);
        if (!copy.Ok)
        {
            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        if (!copy.Files.Any(f => string.Equals(f.RelativePath, TaskPlan.ExecutableName, StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(StepOutcomes.NotAttempted("copy-app", TaskPlan.ExecutableName + " is not in the application folder."));
            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        string? old = null;
        if (Directory.Exists(install))
        {
            old = Path.Combine(parent, Path.GetFileName(install) + ".old-" + suffix);
            if (!FileSteps.MoveFolder(install, old, "move-old-install", steps))
            {
                FileSteps.DeleteTree(staging, "remove-staging", steps);
                return false;
            }
        }

        if (!FileSteps.MoveFolder(staging, install, "move-install", steps))
        {
            if (old is not null)
            {
                FileSteps.MoveFolder(old, install, "restore-old-install", steps);
            }

            FileSteps.DeleteTree(staging, "remove-staging", steps);
            return false;
        }

        if (old is not null)
        {
            // Leaving the old copy behind is not unsafe (it sits under Program Files), so it does not stop install.
            FileSteps.DeleteTree(old, "remove-old-install", steps);
        }

        return CheckInstallFolder(install, steps);
    }

    private bool CheckInstallFolder(string install, List<StepOutcome> steps)
    {
        StepOutcome read = _folders.ReadSddl(install, out string? sddl);
        steps.Add(read);
        if (!read.Ok)
        {
            return false;
        }

        IReadOnlyList<string> problems = AclCheck.CheckInstallFolder(sddl);
        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted("install-folder-acl", problem));
        }

        return problems.Count == 0;
    }

    private bool PrepareMachineFolder(List<StepOutcome> steps)
    {
        string machine = _layout.MachineFolder;
        bool existed = Directory.Exists(machine) || File.Exists(machine);

        if (existed && !RemoveIfNotAPlainFolder(machine, steps))
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

        IReadOnlyList<string> problems = ReadMachineFolderProblems(machine, steps);
        if (problems.Count == 0)
        {
            return true;
        }

        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted("machine-folder-acl", problem));
        }

        if (!existed)
        {
            return false;
        }

        // A folder someone else created first: remove it with whatever it holds and create it once more.
        if (!FileSteps.DeleteTree(machine, "remove-untrusted-machine-folder", steps))
        {
            return false;
        }

        StepOutcome again = _folders.CreateHardened(machine);
        steps.Add(again);
        if (!again.Ok)
        {
            return false;
        }

        problems = ReadMachineFolderProblems(machine, steps);
        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted("machine-folder-acl", problem));
        }

        return problems.Count == 0;
    }

    private IReadOnlyList<string> ReadMachineFolderProblems(string machine, List<StepOutcome> steps)
    {
        StepOutcome read = _folders.ReadSddl(machine, out string? sddl);
        steps.Add(read);
        return read.Ok ? AclCheck.CheckMachineFolder(sddl) : ["The folder security could not be read."];
    }

    // A file, or a junction or link, where the machine folder should be is removed (the link itself, never
    // its target) so a real folder can be created.
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

        bool blockAtBoot = existing.IsOk && existing.Value is not null ? existing.Value.BlockAtBoot : new GateConfig().BlockAtBoot;
        StepOutcome config = store.WriteConfig(new GateConfig { BlockAtBoot = blockAtBoot });
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
            RemoveTasks(steps);
            return false;
        }

        foreach (TaskSpec spec in specs)
        {
            hr = _tasks.Register(TaskPlan.FolderPath, spec, steps);
            steps.Add(StepOutcomes.FromHResult("task-register:" + spec.Name, hr));
            if (hr < 0)
            {
                RemoveTasks(steps);
                return false;
            }
        }

        foreach (TaskSpec spec in specs)
        {
            hr = _tasks.ReadTask(spec.Path, out string? sddl, out string? xml);
            steps.Add(StepOutcomes.FromHResult("task-read:" + spec.Name, hr));
            problems = hr < 0
                ? ["The task could not be read back."]
                : AclCheck.CheckTask(sddl, request.UserSid, spec.UserMayRun).Concat(TaskXmlCheck.Verify(xml, spec, _accountToSid)).ToList();
            if (!Accept("task-verify:" + spec.Name, problems, steps))
            {
                RemoveTasks(steps);
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

// File operations shared by install and uninstall, each recorded as a step.
internal static class FileSteps
{
    // Deletes a folder and everything in it. Directory.Delete removes a junction or link inside the tree
    // without following it. A folder that does not exist counts as deleted.
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
