using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// Schedules a file or an empty folder for deletion at the next restart.
internal interface IRebootDelete
{
    StepOutcome ScheduleDelete(string path);
}

// MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT). Needs an administrator or Local System; a folder is
// removed at restart only if it is empty by then, so files are scheduled before their folders.
// https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
internal sealed partial class MoveFileRebootDelete : IRebootDelete
{
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    public StepOutcome ScheduleDelete(string path)
    {
        if (MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT))
        {
            return StepOutcomes.FromWin32("delete-at-restart", 0, path);
        }

        return StepOutcomes.FromWin32("delete-at-restart", unchecked((uint)Marshal.GetLastPInvokeError()), path);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
}

// uninstall, run elevated from the tray with one UAC prompt. Each reversal is a step and a failure does not
// stop the ones after it, so as much as possible is undone:
//   1. read %ProgramData%\Earshot's security back; a folder that fails the check is not trusted, so its
//      files are not read, no node or service is changed from them, and the folder is not deleted;
//   2. enable every target node at problem 22, using the identity in device.json;
//   3. re-enable the Bluetooth services protection.json lists (the protection feature's hook);
//   4. delete \Earshot\Gate, \Earshot\Protect, \Earshot\BootBlock, anything else in \Earshot, and the folder;
//   5. remove %ProgramData%\Earshot;
//   6. remove %ProgramFiles%\Earshot after the same kind of check, or schedule it for the next restart when
//      it is in use (for example when uninstall runs from that copy).
// A folder is only deleted from this elevated process after its security shows no one but administrators
// can change what is inside, because a recursive delete goes by path. A folder that fails is left for the
// user to remove and the result is partial. Pairing is never touched, and %APPDATA%\Earshot stays for the
// user. The tray removes its own startup value.
internal sealed class UninstallActions
{
    private readonly InstallLayout _layout;
    private readonly IFolderSecurity _folders;
    private readonly INodeApi _nodes;
    private readonly ITaskRegistrar _tasks;
    private readonly IRebootDelete _rebootDelete;
    private readonly ILog _log;

    public UninstallActions(InstallLayout layout, IFolderSecurity folders, INodeApi nodes, ITaskRegistrar tasks, IRebootDelete rebootDelete, ILog log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(rebootDelete);
        ArgumentNullException.ThrowIfNull(log);
        _layout = layout;
        _folders = folders;
        _nodes = nodes;
        _tasks = tasks;
        _rebootDelete = rebootDelete;
        _log = log;
    }

    public InstallResult Run()
    {
        var steps = new List<StepOutcome>();
        bool complete = true;
        string machine = _layout.MachineFolder;
        bool machineExists = Directory.Exists(machine) || File.Exists(machine);
        bool machineTrusted = machineExists &&
                              FolderTrust.IsTrusted(_folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", steps);

        if (machineTrusted)
        {
            var store = new GateStore(machine);
            GateRead<DeviceIdentity> identity = store.ReadDevice();
            steps.Add(identity.Step);
            if (identity.IsOk && identity.Value is not null)
            {
                complete &= AllowNodes(identity.Value, steps);
                complete &= RestoreProtection(identity.Value, store, steps);
            }
            else if (identity.Status != GateReadStatus.Missing)
            {
                // Without a trusted identity no node is changed; say so rather than guess.
                steps.Add(StepOutcomes.NotAttempted("allow-nodes", "device.json is not valid, so no node was enabled."));
                complete = false;
            }
        }
        else if (machineExists)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                machine + " is not safe, so device.json and protection.json were not used and no node or service was changed."));
            complete = false;
        }

        complete &= RemoveTasks(steps);

        if (machineTrusted)
        {
            complete &= FileSteps.DeleteTree(machine, "remove-machine-folder", steps);
        }
        else if (machineExists)
        {
            steps.Add(StepOutcomes.NotAttempted("remove-machine-folder", "Remove " + machine + " by hand."));
            complete = false;
        }

        complete &= RemoveInstallFolder(steps);

        _log.Info("uninstall: " + (complete ? "complete" : "partial") + ".");
        return new InstallResult(complete ? GateExitCode.Success : GateExitCode.Partial, steps);
    }

    private bool AllowNodes(DeviceIdentity identity, List<StepOutcome> steps)
    {
        NodeScanResult scan = NodeScan.FindTargets(_nodes, identity.ContainerId, identity.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            return false;
        }

        if (scan.Targets.Count == 0)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes", "No node matched the pinned device."));
            return true;
        }

        NodeChangeSummary summary = GateActions.ApplyAllow(_nodes, scan.Targets, steps);
        return summary.Outcome == GateExitCode.Success;
    }

    private bool RestoreProtection(DeviceIdentity identity, GateStore store, List<StepOutcome> steps)
    {
        var ctx = new GateRunContext("uninstall", Guid.NewGuid().ToString("N"), identity, _nodes, store, _log, steps);
        if (GateActions.TryRestoreProtection(ctx))
        {
            return ctx.Outcome == GateExitCode.Success;
        }

        GateRead<ProtectionRecord> record = store.ReadProtection();
        if (record.Status == GateReadStatus.Missing)
        {
            return true;
        }

        steps.Add(record.Step);
        if (record.IsOk && record.Value is { DisabledServices.Count: 0 })
        {
            return true;
        }

        steps.Add(StepOutcomes.NotAvailable("protection-restore", "This build cannot re-enable the services in protection.json."));
        return false;
    }

    private bool RemoveTasks(List<StepOutcome> steps)
    {
        bool ok = true;
        int hr = _tasks.ListTasks(TaskPlan.FolderPath, out IReadOnlyList<string> names);
        if (hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
        {
            steps.Add(StepOutcomes.FromHResult("task-folder-list", hr, "No task folder.", ok: true));
            return true;
        }

        steps.Add(StepOutcomes.FromHResult("task-folder-list", hr));
        IEnumerable<string> toDelete = hr < 0
            ? TaskPlan.TaskNames
            : TaskPlan.TaskNames.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string name in toDelete)
        {
            int deleteHr = _tasks.DeleteTask(TaskPlan.FolderPath, name);
            bool gone = deleteHr >= 0 || deleteHr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND;
            steps.Add(StepOutcomes.FromHResult("task-delete:" + name, deleteHr, ok: gone));
            ok &= gone;
        }

        int folderHr = _tasks.DeleteFolder("\\", TaskPlan.FolderName);
        bool folderGone = folderHr >= 0 || folderHr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND;
        steps.Add(StepOutcomes.FromHResult("task-folder-delete", folderHr, ok: folderGone));
        return ok && folderGone;
    }

    private bool RemoveInstallFolder(List<StepOutcome> steps)
    {
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));
        string running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder));
        if (!Directory.Exists(install))
        {
            return true;
        }

        // Deleting at restart goes by path too, so the same check comes before either kind of delete.
        if (!FolderTrust.IsTrusted(_folders, install, AclCheck.CheckInstallFolder, "install-folder-acl", steps))
        {
            steps.Add(StepOutcomes.NotAttempted("remove-install-folder", "Remove " + install + " by hand."));
            return false;
        }

        if (!IntegrityCopy.IsInside(running, install) && FileSteps.DeleteTree(install, "remove-install-folder", steps))
        {
            return true;
        }

        return ScheduleTree(install, steps);
    }

    // Schedules every file, then every folder deepest first, then the folder itself.
    private bool ScheduleTree(string root, List<StepOutcome> steps)
    {
        bool ok = true;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (string file in Directory.EnumerateFiles(root, "*", options))
            {
                StepOutcome step = _rebootDelete.ScheduleDelete(file);
                steps.Add(step);
                ok &= step.Ok;
            }

            foreach (string folder in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length))
            {
                StepOutcome step = _rebootDelete.ScheduleDelete(folder);
                steps.Add(step);
                ok &= step.Ok;
            }
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("delete-at-restart", ex.HResult, root + ": " + ex.Message));
            ok = false;
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("delete-at-restart", ex.HResult, root + ": " + ex.Message));
            ok = false;
        }

        StepOutcome rootStep = _rebootDelete.ScheduleDelete(root);
        steps.Add(rootStep);
        return ok && rootStep.Ok;
    }
}
