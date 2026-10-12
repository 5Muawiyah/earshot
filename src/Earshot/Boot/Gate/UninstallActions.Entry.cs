using Earshot.Contracts;
using Earshot.Install;

namespace Earshot.Boot.Gate;

// The Installed apps entry's part of uninstall. The entry is removed only once the uninstall has finished: a partial uninstall
// leaves files, records or nodes that another run has to deal with, and the entry is how the person finds the way to run it again.
internal static class UninstallActionsEntry
{
    public static InstallResult RemoveEntry(InstallResult result, IUninstallRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(registry);
        if (result.Outcome != GateExitCode.Success)
        {
            return result;
        }

        StepOutcome removed = InstalledAppsEntry.Remove(registry);
        return new InstallResult(removed.Ok ? GateExitCode.Success : GateExitCode.Partial, [.. result.Steps, removed]);
    }
}
