using System.Runtime.InteropServices;
using Earshot.AudioProtection;
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
//   2. enable every target node that is disabled or still carries the persistent disable flag, using the
//      identity in device.json;
//   3. re-enable the Bluetooth services protection.json lists (the protection feature's hook);
//      steps 2 and 3 run inside the machine-wide gate run lock and the device change lock, so they never
//      overlap a gate run, a node change or a service change; without both locks neither step runs;
//   4. delete \Earshot\Gate, \Earshot\Protect, \Earshot\BootBlock, anything else in \Earshot, and the folder;
//   5. remove %ProgramData%\Earshot, unless step 2 or 3 did not finish: then device.json and protection.json are
//      the only record of what to allow and turn back on, so the folder (still protected) is kept with those
//      two files and everything else in it is removed, and uninstall can be run again;
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
    private readonly IGateRunLock _runLock;
    private readonly Func<TimeSpan, bool> _wait;
    private readonly TimeSpan _lockWait;

    // How long uninstall waits for the gate run lock and the device change lock: longer than \Earshot\Protect's
    // PT5M limit, so a protect verb that holds either has finished or been stopped by the scheduler.
    public static readonly TimeSpan LockWait = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15);

    private readonly IBluetoothServiceApi? _bluetooth;

    // The files kept when a node or a service could not be restored.
    internal static readonly IReadOnlySet<string> KeptRecords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "device.json", "protection.json" };

    // runLock null: no machine-wide lock, for a test's fake node table. bluetooth null: the protection restore is
    // not available. Program passes the mutex and the real Bluetooth API.
    public UninstallActions(
        InstallLayout layout, IFolderSecurity folders, INodeApi nodes, ITaskRegistrar tasks, IRebootDelete rebootDelete, ILog log,
        IGateRunLock? runLock = null, IBluetoothServiceApi? bluetooth = null)
        : this(layout, folders, nodes, tasks, rebootDelete, log, runLock, DeviceChangeLock.SleepAndContinue, LockWait, bluetooth)
    {
    }

    // For tests: the wait between device change lock attempts and how long to wait.
    internal UninstallActions(
        InstallLayout layout, IFolderSecurity folders, INodeApi nodes, ITaskRegistrar tasks, IRebootDelete rebootDelete, ILog log,
        IGateRunLock? runLock, Func<TimeSpan, bool> wait, TimeSpan lockWait, IBluetoothServiceApi? bluetooth = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(rebootDelete);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(wait);
        GateActions.RequireSameMachine(nodes, bluetooth);
        _layout = layout;
        _folders = folders;
        _nodes = nodes;
        _tasks = tasks;
        _rebootDelete = rebootDelete;
        _log = log;
        _runLock = runLock ?? NoGateRunLock.Instance;
        _wait = wait;
        _lockWait = lockWait;
        _bluetooth = bluetooth;
    }

    internal IGateRunLock RunLock => _runLock;

    public InstallResult Run()
    {
        var steps = new List<StepOutcome>();
        bool complete = true;
        string machine = _layout.MachineFolder;
        bool machineExists = Directory.Exists(machine) || File.Exists(machine);
        bool machineTrusted = machineExists &&
                              FolderTrust.IsTrusted(_folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", steps);

        // False when a node or a service may still need restoring, so its record must stay.
        bool nodesRestored = true;
        bool servicesRestored = true;
        if (machineTrusted)
        {
            var store = new GateStore(machine);
            GateRead<DeviceIdentity> identity = store.ReadDevice();
            steps.Add(identity.Step);
            if (identity.IsOk && identity.Value is not null)
            {
                // Both locks are released before the machine folder is deleted, since that deletes the lock file.
                using IDisposable? running = _runLock.TryEnter(_lockWait, steps);
                using DeviceChangeLock? changing = running is null
                    ? null
                    : DeviceChangeLock.TryAcquire(machine, DeviceChangeLockAccess.For(_nodes), _lockWait, _wait, steps);
                if (changing is null)
                {
                    steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                        "The Earshot change locks could not be taken, so no node or service was changed. Run uninstall again."));
                    nodesRestored = false;
                    servicesRestored = ProtectionRecordIsEmpty(store, steps);
                }
                else
                {
                    nodesRestored = AllowNodes(identity.Value, steps);
                    servicesRestored = RestoreProtection(identity.Value, store, steps);
                }
            }
            else if (identity.Status != GateReadStatus.Missing)
            {
                // Without a trusted identity no node is changed; say so rather than guess.
                steps.Add(StepOutcomes.NotAttempted("allow-nodes", "device.json is not valid, so no node was enabled."));
                complete = false;
                servicesRestored = ProtectionRecordIsEmpty(store, steps);
                if (!servicesRestored)
                {
                    steps.Add(StepOutcomes.NotAttempted("protection-restore", "device.json is not valid, so no service was turned back on."));
                }
            }
            else
            {
                servicesRestored = ProtectionRecordIsEmpty(store, steps);
                if (!servicesRestored)
                {
                    steps.Add(StepOutcomes.NotAttempted("protection-restore", "device.json is missing, so no service was turned back on."));
                }
            }

            complete &= nodesRestored && servicesRestored;
        }
        else if (machineExists)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                machine + " is not safe, so device.json and protection.json were not used and no node or service was changed."));
            complete = false;
        }

        complete &= RemoveTasks(steps);

        if (machineTrusted && nodesRestored && servicesRestored)
        {
            complete &= FileSteps.DeleteTree(machine, "remove-machine-folder", steps);
        }
        else if (machineTrusted)
        {
            KeepRecords(machine, steps);
            complete = false;
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

    // Complete when every target is enabled without the persistent disable flag, or not present without it.
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
        return summary.Failed == 0;
    }

    private bool RestoreProtection(DeviceIdentity identity, GateStore store, List<StepOutcome> steps)
    {
        var ctx = new GateRunContext("uninstall", Guid.NewGuid().ToString("N"), identity, _nodes, store, _log, steps, _bluetooth);
        if (GateActions.TryRestoreProtection(ctx))
        {
            return ctx.Outcome == GateExitCode.Success;
        }

        if (ProtectionRecordIsEmpty(store, steps))
        {
            return true;
        }

        steps.Add(StepOutcomes.NotAvailable("protection-restore", "This build cannot re-enable the services in protection.json."));
        return false;
    }

    // True when protection.json is missing or lists no service. An unreadable or invalid file is not empty: it
    // may be the only record of what to turn back on.
    private static bool ProtectionRecordIsEmpty(GateStore store, List<StepOutcome> steps)
    {
        GateRead<ProtectionRecord> record = store.ReadProtection();
        if (record.Status == GateReadStatus.Missing)
        {
            return true;
        }

        if (record.IsOk && record.Value is { DisabledServices.Count: 0 })
        {
            return true;
        }

        steps.Add(record.Step);
        return false;
    }

    // Keeps the machine folder with device.json and protection.json and removes everything else in it. The
    // folder's security was read back, so nobody but administrators can swap an entry while this runs.
    private static void KeepRecords(string machine, List<StepOutcome> steps)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(machine).EnumerateFileSystemInfos().ToList();
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("remove-machine-folder", ex.HResult, machine + ": " + ex.Message));
            entries = [];
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("remove-machine-folder", ex.HResult, machine + ": " + ex.Message));
            entries = [];
        }

        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo folder)
            {
                FileSteps.DeleteTree(folder.FullName, "remove-machine-folder", steps);
                continue;
            }

            if (KeptRecords.Contains(entry.Name))
            {
                continue;
            }

            try
            {
                entry.Delete();
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult("remove-machine-folder", ex.HResult, entry.FullName + ": " + ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult("remove-machine-folder", ex.HResult, entry.FullName + ": " + ex.Message));
            }
        }

        steps.Add(StepOutcomes.NotAttempted("remove-machine-folder",
            machine + " is kept with device.json and protection.json, because a device node or a Bluetooth service could not be " +
            "restored. Run uninstall again, or turn Handsfree back on in Windows Bluetooth settings."));
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
