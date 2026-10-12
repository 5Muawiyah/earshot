using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// The scheduled task the first, unpackaged version registered in the root task folder.
internal sealed partial class UninstallActions
{
    internal const string PrototypeTaskName = "AirPodsGate";

    private enum PrototypeTaskState
    {
        NotThere,
        Earshots,
        SomeoneElses,
    }

    private PrototypeTaskState? _prototype;

    // The task's action is read once. It is Earshot's only when it runs exactly the installed Earshot.exe (the same way
    // TaskXmlCheck compares an action: quotes trimmed, case ignored). A task of that name that runs anything else, a script in
    // someone's source folder for instance, may be something the owner wrote, and is not deleted.
    private PrototypeTaskState PrototypeTask(List<StepOutcome> steps)
    {
        if (_prototype is { } known)
        {
            return known;
        }

        int hr = _tasks.ReadTask("\\" + PrototypeTaskName, out _, out string? xml);
        if (hr < 0)
        {
            // A task that is not there answers file not found; a missing folder answers path not found.
            if (hr != TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND && hr != unchecked((int)0x80070003))
            {
                steps.Add(StepOutcomes.FromHResult("task-prototype-read", hr, "The old gate task could not be read, so it was left."));
            }

            _prototype = PrototypeTaskState.NotThere;
            return _prototype.Value;
        }

        string expected = Path.Combine(_layout.InstallFolder, TaskPlan.ExecutableName);
        string? command = TaskXmlCheck.Describe(xml).Command?.Trim().Trim('"');
        _prototype = string.Equals(command, expected, StringComparison.OrdinalIgnoreCase)
            ? PrototypeTaskState.Earshots
            : PrototypeTaskState.SomeoneElses;
        return _prototype.Value;
    }

    private bool RemovePrototypeTask(List<StepOutcome> steps)
    {
        switch (PrototypeTask(steps))
        {
            case PrototypeTaskState.Earshots:
                int hr = _tasks.DeleteTask("\\", PrototypeTaskName);
                bool gone = hr >= 0 || hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND;
                steps.Add(StepOutcomes.FromHResult("task-delete:" + PrototypeTaskName, hr, ok: gone));
                _report.Note(gone ? "Removed the old scheduled task " + PrototypeTaskName + "." : "The old scheduled task " + PrototypeTaskName + " could not be removed.");
                return gone;
            case PrototypeTaskState.SomeoneElses:
                steps.Add(new StepOutcome("task-prototype", true, NativeCodes.NotAttempted, NativeCodes.Name(NativeCodes.NotAttempted),
                    "The task " + PrototypeTaskName + " does not run the installed Earshot, so it was left."));
                _report.Note("The scheduled task " + PrototypeTaskName + " was left, because it does not run Earshot.");
                return true;
            default:
                return true;
        }
    }
}
