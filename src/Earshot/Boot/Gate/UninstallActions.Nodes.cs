using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// Putting the AirPods nodes back, from any state an earlier version may have left them in, and choosing which nodes those are
// when no record names them.
internal sealed partial class UninstallActions
{
    private const string FlagClearStep = "node-flag-clear:";

    private static readonly string[] MachineRecordFiles = ["config.json", "device.json", "protection.json"];

    // Evidence, read from this machine only, that Earshot was set up here. Without any of it nothing about the AirPods is
    // Earshot's to undo: a node the owner disabled by hand looks the same as one Earshot blocked.
    private sealed record Footprint(bool MachineFiles, bool Tasks, bool Service, bool InstallFolder, bool PrototypeTask, bool SettingsPin, bool Chosen)
    {
        public bool Any => MachineFiles || Tasks || Service || InstallFolder || PrototypeTask || SettingsPin || Chosen;

        public string Describe()
        {
            var found = new List<string>();
            AddIf(found, MachineFiles, "files in the machine folder");
            AddIf(found, Tasks, "scheduled tasks");
            AddIf(found, Service, "the hand-back service");
            AddIf(found, InstallFolder, "the install folder");
            AddIf(found, PrototypeTask, "the old gate task");
            AddIf(found, SettingsPin, "the user's settings");
            AddIf(found, Chosen, "the AirPods the user chose");
            return string.Join(", ", found);
        }

        private static void AddIf(List<string> into, bool present, string what)
        {
            if (present)
            {
                into.Add(what);
            }
        }
    }

    private Footprint DetectFootprint(string machine, List<StepOutcome> steps)
    {
        bool files = false;
        try
        {
            files = MachineRecordFiles.Any(name => File.Exists(Path.Combine(machine, name))) ||
                    (Directory.Exists(machine) && Directory.EnumerateFiles(machine, "status-*.json").Any());
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("footprint-machine-files", ex.HResult, machine + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("footprint-machine-files", ex.HResult, machine + ": " + ex.Message));
        }

        // A task folder that exists is Earshot's: nothing else creates a folder of that name.
        bool tasks = _tasks.ListTasks(TaskPlan.FolderPath, out _) >= 0;
        bool install = Directory.Exists(_layout.InstallFolder);
        bool prototype = PrototypeTask(steps) == PrototypeTaskState.Earshots;
        var footprint = new Footprint(
            files, tasks, _serviceWasRegistered, install, prototype, Options.SettingsPinAirPods, Options.ChosenAddress is not null);
        steps.Add(new StepOutcome("uninstall-footprint", true, 0, "S_OK",
            footprint.Any ? "Earshot was set up here: " + footprint.Describe() + "." : "Nothing shows that Earshot was set up here."));
        return footprint;
    }

    // Nodes for the identity rule, or none. Reads the device list; changes a node only through RestoreNodes, and never
    // disables one. Returns true when everything that had to be put back was, or there was nothing it was Earshot's to put back.
    private bool RestoreWithoutRecords(string machine, List<StepOutcome> steps)
    {
        Footprint footprint = DetectFootprint(machine, steps);
        if (!footprint.Any)
        {
            _report.Note("Nothing shows that Earshot was set up on this PC, so no AirPods setting was changed.");
            return true;
        }

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
        steps.AddRange(found.Steps);
        if (!found.Listed)
        {
            _report.NotPutBack("Windows' list of devices could not be read, so the AirPods were not checked.");
            return false;
        }

        IReadOnlyList<AirPodsCandidate> candidates = found.Candidates;
        if (candidates.Count == 0)
        {
            _report.Note("No AirPods are known to Windows, so there was nothing to turn back on.");
            return true;
        }

        string? chosen = Options.ChosenAddress;
        AirPodsCandidate? pick = chosen is not null
            ? candidates.FirstOrDefault(c => string.Equals(c.Address, chosen, StringComparison.OrdinalIgnoreCase))
            : candidates.Count == 1 ? candidates[0] : null;
        if (pick is null)
        {
            string names = string.Join(", ", candidates.Select(NameOf));
            steps.Add(StepOutcomes.NotAttempted("allow-nodes",
                "More than one AirPods is paired and there is no record of which one Earshot blocked, so none was changed."));
            _report.Choose(candidates.Select(NameOf));
            _report.Note("Earshot left no record of which AirPods it blocked, and more than one is paired, so none was changed: " + names + ".");
            return false;
        }

        NodeScanResult scan = NodeScan.FindTargets(_nodes, pick.ContainerId, pick.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            return false;
        }

        if (scan.Targets.Count == 0)
        {
            return scan.Unreadable.Count == 0;
        }

        bool restored = RestoreNodes(scan.Targets, steps);
        return restored && scan.Unreadable.Count == 0;
    }

    private static string NameOf(AirPodsCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Name) ? "AirPods" : candidate.Name;

    // Each target is put back: a node in the tree that is disabled or still flagged is enabled, a node out of the tree that
    // still carries CONFIGFLAG_DISABLED has the flag cleared through SetupAPI (CM_Enable_DevNode needs a node that is there,
    // and the flag decides what the node does when Windows reports it again). Nodes that are fine are left alone. Every
    // target is read again afterwards, and one that is still disabled, still flagged or unreadable is a failure.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    private bool RestoreNodes(IReadOnlyList<TargetNode> targets, List<StepOutcome> steps)
    {
        var scratch = new List<StepOutcome>();
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TargetNode target in targets)
        {
            BluetoothNode before = NodeStateReader.ReadNode(_nodes, target, scratch, out bool complete);
            if (!complete || NeedsRestore(before))
            {
                needed.Add(target.InstanceId);
            }
        }

        // Away nodes first: with their flag gone they come back enabled by themselves when the device node is enabled.
        var present = new List<TargetNode>();
        foreach (TargetNode target in targets)
        {
            if (_nodes.Locate(target.InstanceId, includeNonPresent: false, out _) == CfgMgr32.CR_NO_SUCH_DEVNODE)
            {
                ClearAwayFlag(target, steps);
            }
            else
            {
                present.Add(target);
            }
        }

        NodeChangeSummary summary = present.Count == 0 ? new NodeChangeSummary(0, 0, 0, 0, 0) : GateActions.ApplyAllow(_nodes, present, steps);

        var restored = new List<TargetNode>();
        var failed = new List<TargetNode>();
        foreach (TargetNode target in targets)
        {
            BluetoothNode after = NodeStateReader.ReadNode(_nodes, target, scratch, out bool complete);
            if (!complete || NeedsRestore(after))
            {
                failed.Add(target);
            }
            else if (needed.Contains(target.InstanceId))
            {
                restored.Add(target);
            }
        }

        if (restored.Count > 0)
        {
            _report.Nodes(restored, restored: true);
        }

        if (failed.Count > 0)
        {
            _report.Nodes(failed, restored: false);
        }
        else if (summary.Failed > 0)
        {
            _report.UnnamedNodeFailure();
        }

        return failed.Count == 0 && summary.Failed == 0;
    }

    // Disabled in the tree, or flagged whether it is in the tree or not.
    private static bool NeedsRestore(BluetoothNode node) =>
        node.ConfigFlagsDisabledBit || (node.IsPresent && node.Status != NodeBlockStatus.Enabled);

    private void ClearAwayFlag(TargetNode target, List<StepOutcome> steps)
    {
        string id = target.InstanceId;
        uint read = _nodes.GetConfigFlags(target.PhantomDevInst, out uint flags);
        if (read == CfgMgr32.CR_NO_SUCH_VALUE || (read == CfgMgr32.CR_SUCCESS && (flags & CfgMgr32.CONFIGFLAG_DISABLED) == 0))
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, CfgMgr32.CR_SUCCESS, "Not present and not marked disabled, so there is nothing to clear."));
            return;
        }

        if (read != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-configflags:" + id, read, "Whether it is marked disabled could not be read, so it was not changed."));
            return;
        }

        ConfigFlagsChange change = _nodes.ClearDisabledFlag(id);
        bool took = change.Ok && !change.DisabledAfter;
        string detail = took
            ? "Not in the tree; the disabled flag was cleared."
            : change.Ok
                ? "The flag change did not show when read back (flags " + change.Before.ToString("X", CultureInfo.InvariantCulture) +
                  " then " + change.After.ToString("X", CultureInfo.InvariantCulture) + ")."
                : "Stopped at " + change.Stage + ".";
        steps.Add(StepOutcomes.FromWin32(FlagClearStep + id, change.Win32Error, detail, ok: took));
    }
}
