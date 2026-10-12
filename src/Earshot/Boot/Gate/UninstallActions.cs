using System.Runtime.InteropServices;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;

namespace Earshot.Boot.Gate;

// Schedules a file or an empty folder for deletion at the next restart.
internal interface IRebootDelete
{
    StepOutcome ScheduleDelete(string path);
}

// MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT). Needs an administrator or Local System; a folder is
// removed at restart only if it is empty by then, so files are scheduled before their folders.
// https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
internal sealed class MoveFileRebootDelete : IRebootDelete
{
    public StepOutcome ScheduleDelete(string path)
    {
        if (FileApis.MoveFileEx(path, null, FileApis.MOVEFILE_DELAY_UNTIL_REBOOT))
        {
            return StepOutcomes.FromWin32("delete-at-restart", 0, path);
        }

        return StepOutcomes.FromWin32("delete-at-restart", unchecked((uint)Marshal.GetLastPInvokeError()), path);
    }
}

// uninstall, run elevated from the tray with one UAC prompt. Each reversal is a step and a failure does not
// stop the ones after it, so as much as possible is undone:
//   0. stop and delete the hand-back service, before anything is enabled: a service still running could block the nodes
//      again at a shut down that starts after they were enabled, and its program runs from the install folder, so once it
//      has stopped (and its process has gone) the folder can be deleted now instead of at the next restart;
//   1. read %ProgramData%\Earshot's security back; a folder that fails the check is not trusted, so its
//      files are not read, no node or service is changed from them, and the folder is not deleted;
//   2. put back every target node that is disabled or still carries the persistent disable flag: enable it when it is in
//      the tree, clear the flag when it has left the tree. The targets are the identity in device.json; when there is none,
//      the AirPods Windows knows by the identity rule, but only when Earshot left a footprint on this machine (files, tasks,
//      the hand-back service, the install folder), only the one pair when several are paired and the user chose it, and
//      only ever to restore, never to disable;
//   3. re-enable the Bluetooth services protection.json lists (the protection feature's hook);
//      steps 2 and 3 run inside the machine-wide gate run lock and the device change lock, so they never
//      overlap a gate run, a node change or a service change; without both locks neither step runs;
//   4. once both finished, and still inside both locks, delete device.json, config.json, protection.json and
//      protection-intent.json, so no later gate run has an identity or a setting to act on;
//   5. delete \Earshot\Gate, \Earshot\Protect, \Earshot\BootBlock, anything else in \Earshot, and the folder, and the
//      first version's \AirPodsGate when it runs exactly the installed Earshot.exe. A
//      shut down that starts during the uninstall finds the service already gone; and were it still to come, it reads the
//      same run lock as every elevated run, which this uninstall holds until the machine folder is gone, so it would find
//      it held, or find no config.json, and change nothing;
//   6. remove %ProgramData%\Earshot, unless step 2 or 3 did not finish (or the run lock could not be entered):
//      then device.json and protection.json are the only record of what to allow and turn back on, so the
//      folder (still protected) is kept with those two files and everything else in it is removed, and
//      uninstall can be run again;
//   7. remove %ProgramFiles%\Earshot after the same kind of check, or schedule it for the next restart when
//      it is in use (for example when uninstall runs from that copy);
//   8. remove the Installed apps entry (RemoveInstalledAppsEntry, once the install side writes one).
// The gate run lock is held from before device.json is read until step 6 has finished. A gate run that was
// already waiting for it (a block from the tray, the session-end block, the boot block or a protect verb) gets
// in only once the records and the folder are gone, so it changes nothing, and it can never undo the restore
// between the restore and the removal. The device change lock is a file in the machine folder, so it is let go
// after step 4 and before the folder is removed.
// A folder is only deleted from this elevated process after its security shows no one but administrators
// can change what is inside, because a recursive delete goes by path. A folder that fails is left for the
// user to remove and the result is partial. Pairing is never touched, and %APPDATA%\Earshot stays for the
// user. The Open on startup value is the tray's and is not touched here: the owner turns it off in Earshot first
// (Program.RunUninstall logs this), and the tray removes one left behind whose program is gone when it next starts
// with the setting off.
internal sealed partial class UninstallActions
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
    private readonly IServiceControl? _service;

    // The report of the current run, whether the hand-back service was registered when the run began, and what the caller
    // knows that this elevated run cannot read.
    private UninstallReportBuilder _report = new();
    private bool _serviceWasRegistered;

    internal UninstallOptions Options { get; init; } = new();

    // The files kept when a node or a service could not be restored.
    internal static readonly IReadOnlySet<string> KeptRecords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "device.json", "protection.json" };

    internal const string RetireStepPrefix = "retire-record:";

    // runLock null: no machine-wide lock, for a test's fake node table. bluetooth null: the protection restore is
    // not available. Program passes the mutex and the real Bluetooth API.
    public UninstallActions(
        InstallLayout layout, IFolderSecurity folders, INodeApi nodes, ITaskRegistrar tasks, IRebootDelete rebootDelete, ILog log,
        IGateRunLock? runLock = null, IBluetoothServiceApi? bluetooth = null, IServiceControl? service = null)
        : this(layout, folders, nodes, tasks, rebootDelete, log, runLock, DeviceChangeLock.SleepAndContinue, LockWait, bluetooth, service)
    {
    }

    // For tests: the wait between device change lock attempts and how long to wait.
    internal UninstallActions(
        InstallLayout layout, IFolderSecurity folders, INodeApi nodes, ITaskRegistrar tasks, IRebootDelete rebootDelete, ILog log,
        IGateRunLock? runLock, Func<TimeSpan, bool> wait, TimeSpan lockWait, IBluetoothServiceApi? bluetooth = null,
        IServiceControl? service = null)
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
        _service = service;
    }

    internal IGateRunLock RunLock => _runLock;

    internal IServiceControl? Service => _service;

    // As in install, an unexpected failure is logged and returned with the steps taken so far.
    public InstallResult Run() => RunWithReport().Result;

    // Run, with the plain-words report of what was put back and what could not be.
    public UninstallRun RunWithReport()
    {
        _report = new UninstallReportBuilder();
        _prototype = null;
        _serviceWasRegistered = false;
        var steps = new List<StepOutcome>();
        try
        {
            InstallResult result = RunSteps(steps);
            return new UninstallRun(result, _report.Build());
        }
        catch (Exception ex)
        {
            steps.Add(ElevatedFailure.Step("uninstall", ex));
            _log.Error("uninstall stopped with " + ex.GetType().Name + " after " + steps.Count + " steps.", ex);
            return new UninstallRun(new InstallResult(GateExitCode.Failed, steps), _report.Build());
        }
    }

    private InstallResult RunSteps(List<StepOutcome> steps)
    {
        // First of all, so nothing can block again what the steps below enable. A service that will not stop is still
        // deleted and goes at the next restart, and makes the result partial.
        int serviceFrom = steps.Count;
        bool complete = RemoveService(steps);
        _serviceWasRegistered = steps.Skip(serviceFrom).Any(s => s.Step == ServiceSteps.Delete && s.Code != NativeCodes.NotAttempted);
        string machine = _layout.MachineFolder;
        bool machineExists = Directory.Exists(machine) || File.Exists(machine);
        bool machineTrusted = machineExists &&
                              FolderTrust.IsTrusted(_folders, machine, AclCheck.CheckMachineFolder, "machine-folder-acl", steps);

        if (machineTrusted)
        {
            // Held until the machine folder is removed or kept; see the class comment.
            using IDisposable? running = _runLock.TryEnter(_lockWait, steps);
            complete &= TearDownMachineFolder(machine, running is not null, steps);
        }
        else
        {
            if (machineExists)
            {
                steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                    machine + " is not safe, so device.json and protection.json were not used and no node or service was changed."));
                complete = false;
            }
            else
            {
                complete &= RestoreWithoutMachineFolder(machine, steps);
            }

            complete &= RemoveTasks(steps);

            if (machineExists)
            {
                steps.Add(StepOutcomes.NotAttempted("remove-machine-folder", "Remove " + machine + " by hand."));
                complete = false;
            }
        }

        complete &= RemoveInstallFolder(steps);
        complete &= RemoveEntry(steps);

        _log.Info("uninstall: " + (complete ? "complete" : "partial") + ".");
        return new InstallResult(complete ? GateExitCode.Success : GateExitCode.Partial, steps);
    }

    // The Installed apps entry is removed by the part of this class that knows how it is written; the call is dropped when
    // there is none. Any step it adds that failed makes the result partial.
    partial void RemoveInstalledAppsEntry(IList<StepOutcome> steps);

    private bool RemoveEntry(List<StepOutcome> steps)
    {
        int from = steps.Count;
        RemoveInstalledAppsEntry(steps);
        return steps.Skip(from).All(s => s.Ok);
    }

    // No machine folder means no device.json, but the AirPods nodes may still carry what an earlier version did to them
    // (the folder is removed by a Remove device in Windows, a manual clean-up, or an uninstall that finished the folder and
    // not the nodes). The gate run lock is a named mutex, not a file in that folder, so it can still be held; the device
    // change lock is a file in the folder, and with the folder gone nothing else is changing nodes.
    private bool RestoreWithoutMachineFolder(string machine, List<StepOutcome> steps)
    {
        using IDisposable? running = _runLock.TryEnter(_lockWait, steps);
        if (running is null)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes", "Another Earshot run held the gate run lock, so no node was changed. Run uninstall again."));
            return false;
        }

        return RestoreWithoutRecords(machine, steps);
    }

    // Steps 2 to 6 for a machine folder whose security passed the check. runLockHeld: the gate run lock was
    // entered and is held by the caller until this returns. True when everything was undone.
    private bool TearDownMachineFolder(string machine, bool runLockHeld, List<StepOutcome> steps)
    {
        bool complete = true;
        var store = new GateStore(machine);

        // False when a node or a service may still need restoring, so its record must stay.
        bool nodesRestored = true;
        bool servicesRestored = true;
        GateRead<DeviceIdentity> identity = store.ReadDevice();
        steps.Add(identity.Step);
        if (identity.IsOk && identity.Value is not null)
        {
            // The device change lock is a file in the machine folder, so it is let go before the folder is removed.
            using DeviceChangeLock? changing = runLockHeld
                ? DeviceChangeLock.TryAcquire(machine, DeviceChangeLockAccess.For(_nodes), _lockWait, _wait, steps)
                : null;
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
                if (nodesRestored && servicesRestored)
                {
                    RetireRecords(store, steps);
                }
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
            if (runLockHeld)
            {
                using DeviceChangeLock? changing = DeviceChangeLock.TryAcquire(machine, DeviceChangeLockAccess.For(_nodes), _lockWait, _wait, steps);
                if (changing is null)
                {
                    steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                        "The Earshot change locks could not be taken, so no node was changed. Run uninstall again."));
                    nodesRestored = false;
                }
                else
                {
                    nodesRestored = RestoreWithoutRecords(machine, steps);
                }
            }

            servicesRestored = ProtectionRecordIsEmpty(store, steps);
            if (!servicesRestored)
            {
                steps.Add(StepOutcomes.NotAttempted("protection-restore", "device.json is missing, so no service was turned back on."));
            }
        }

        complete &= nodesRestored && servicesRestored;
        complete &= RemoveTasks(steps);

        if (!runLockHeld)
        {
            // Another elevated Earshot run may still be using the folder, so it is not removed.
            KeepRecords(machine,
                "is kept with device.json and protection.json, because another Earshot run held the gate run lock. Run uninstall again.",
                steps);
            return false;
        }

        if (nodesRestored && servicesRestored)
        {
            return FileSteps.DeleteTree(machine, "remove-machine-folder", steps) && complete;
        }

        KeepRecords(machine,
            "is kept with device.json and protection.json, because a device node or a Bluetooth service could not be " +
            "restored. Run uninstall again, or turn Handsfree back on in Windows Bluetooth settings.",
            steps);
        return false;
    }

    // Deletes the files a gate run acts on: device.json (every node change and protect verb), config.json (the
    // boot block) and the two protection records. Called once everything is restored, while both locks are
    // still held. A file that cannot be deleted is a failed step; the folder removal after it decides the result.
    private static void RetireRecords(GateStore store, List<StepOutcome> steps)
    {
        foreach (string path in new[] { store.DeviceFile, store.ConfigFile, store.ProtectionFile, new ProtectionIntentFile(store.Folder).FilePath })
        {
            string name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                File.Delete(path);
                steps.Add(StepOutcomes.FromHResult(RetireStepPrefix + name, 0, path));
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult(RetireStepPrefix + name, ex.HResult, path + ": " + ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult(RetireStepPrefix + name, ex.HResult, path + ": " + ex.Message));
            }
        }
    }

    // Complete when every target is enabled without the persistent disable flag, or not present without it, and
    // no node that may belong to the device was left unread (it may still be disabled).
    private bool AllowNodes(DeviceIdentity identity, List<StepOutcome> steps)
    {
        NodeScanResult scan = NodeScan.FindTargets(_nodes, identity.ContainerId, identity.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            return false;
        }

        if (scan.Unreadable.Count > 0)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                "The container of " + string.Join(", ", scan.Unreadable) + " could not be read, so it was not enabled and may still be disabled."));
        }

        if (scan.Targets.Count == 0)
        {
            steps.Add(StepOutcomes.NotAttempted("allow-nodes", "No node matched the pinned device."));
            return scan.Unreadable.Count == 0;
        }

        return RestoreNodes(scan.Targets, steps) && scan.Unreadable.Count == 0;
    }

    private bool RestoreProtection(DeviceIdentity identity, GateStore store, List<StepOutcome> steps)
    {
        var ctx = new GateRunContext("uninstall", Guid.NewGuid().ToString("N"), identity, _nodes, store, _log, steps, _bluetooth);
        int from = steps.Count;
        if (GateActions.TryRestoreProtection(ctx))
        {
            if (ctx.Outcome != GateExitCode.Success && PairingIsGone(identity, store, steps, from))
            {
                return true;
            }

            ReportServices(steps, from);
            return ctx.Outcome == GateExitCode.Success;
        }

        if (ProtectionRecordIsEmpty(store, steps))
        {
            return true;
        }

        steps.Add(StepOutcomes.NotAvailable("protection-restore", "This build cannot re-enable the services in protection.json."));
        return false;
    }

    // The services Earshot turned off belong to a pairing, and go with it when the device is removed from Windows. When
    // the device is not paired (the paired list, read in full, does not have it) or the call itself answered
    // ERROR_NOT_FOUND for every service it failed on, there is nothing left to turn on: the list is emptied and the step
    // says why, with the code. A failure of any other kind is not this, and the record stays.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
    private bool PairingIsGone(DeviceIdentity identity, GateStore store, List<StepOutcome> steps, int from)
    {
        GateRead<ProtectionRecord> record = store.ReadProtection();
        if (!record.IsOk || record.Value is not { DisabledServices.Count: > 0 })
        {
            return false;
        }

        List<int> failedCalls = [];
        for (int i = from; i < steps.Count; i++)
        {
            if (steps[i].Step.StartsWith("bt-service-enable:", StringComparison.Ordinal) && !steps[i].Ok)
            {
                failedCalls.Add(i);
            }
        }

        bool callsNotFound = failedCalls.Count > 0 && failedCalls.All(i => steps[i].Code == (int)BluetoothApis.ERROR_NOT_FOUND);
        bool unpaired = false;
        if (!callsNotFound && _bluetooth is not null)
        {
            BluetoothDeviceEntry? paired = BluetoothDeviceLookup.Find(_bluetooth, identity.Address, new List<StepOutcome>(), out bool listFailed);
            unpaired = paired is null && !listFailed;
        }

        if (!callsNotFound && !unpaired)
        {
            return false;
        }

        foreach (int i in failedCalls)
        {
            steps[i] = steps[i] with { Ok = true, Detail = steps[i].Detail + " The device is no longer paired, so there was nothing to turn back on." };
        }

        StepOutcome emptied = store.WriteProtection(new ProtectionRecord());
        steps.Add(emptied);
        if (!emptied.Ok)
        {
            return false;
        }

        steps.Add(StepOutcomes.FromWin32("protection-restore", BluetoothApis.ERROR_NOT_FOUND,
            "The AirPods are no longer paired, so the services Earshot turned off went with the pairing and there is nothing to turn back on.", ok: true));
        _report.Note("The AirPods are no longer paired, so there was nothing to turn back on for call audio.");
        return true;
    }

    // What the services step did, in plain words: call audio turned back on, or not.
    private void ReportServices(List<StepOutcome> steps, int from)
    {
        bool on = false;
        bool failed = false;
        for (int i = from; i < steps.Count; i++)
        {
            if (!steps[i].Step.StartsWith("bt-service-enable:", StringComparison.Ordinal))
            {
                continue;
            }

            on |= steps[i].Ok && steps[i].Code == 0;
            failed |= !steps[i].Ok;
        }

        if (on)
        {
            _report.PutBack("Call audio was turned back on.");
        }

        if (failed)
        {
            _report.NotPutBack("Call audio could not be turned back on.");
        }
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
    // folder's security was read back, so nobody but administrators can swap an entry while this runs. why
    // follows the folder path in the step.
    private static void KeepRecords(string machine, string why, List<StepOutcome> steps)
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

        steps.Add(StepOutcomes.NotAttempted("remove-machine-folder", machine + " " + why));
    }

    private bool RemoveTasks(List<StepOutcome> steps)
    {
        bool ok = RemovePrototypeTask(steps);
        int hr = _tasks.ListTasks(TaskPlan.FolderPath, out IReadOnlyList<string> names);
        if (hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
        {
            steps.Add(StepOutcomes.FromHResult("task-folder-list", hr, "No task folder.", ok: true));
            return ok;
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

    // Stops and deletes the hand-back service. A service that will not stop is still deleted, and goes when the computer
    // restarts, but the result is partial. Without a service control (the tests that are not about the service) there is
    // nothing to remove.
    private bool RemoveService(List<StepOutcome> steps) =>
        _service is null || ServiceRemoval.Remove(_service, _wait, steps);

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
