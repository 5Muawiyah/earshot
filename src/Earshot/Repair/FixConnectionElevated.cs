using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

// Fix connection run from a process that is already an elevated administrator (Setup.exe runs the extracted program's fix verb
// from its admin-only staging folder). Both questions are still asked on the screen and the input-desktop check still stands;
// what changes is that no second administrator prompt is raised and the plan is written where only administrators can reach it.

// Applies the plan in this process. The checks of fix-apply (the token, the environment, the plan file's hash and its
// intersection with what this PC shows) are the same ones the child process makes, because `apply` is that mode's own
// entry point.
internal sealed class InProcessFixElevator : IFixElevator
{
    private readonly Func<string, string, int> _apply;

    public InProcessFixElevator(Func<string, string, int> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;
    }

    public Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (consent.Kind != FixConsentKind.Changes)
        {
            throw new ArgumentException("The apply step needs the consent of the first question.", nameof(consent));
        }

        if (!FixPlanFileReader.IsPlanPath(planPath, out string problem) || !FileManifest.IsSha256(planSha256))
        {
            return Task.FromResult(new ElevatedRun(null, StepOutcomes.NotAttempted("fix-apply", "The plan file or its hash is not valid: " + problem)));
        }

        return Task.Run(() =>
        {
            int exit = _apply(planPath, planSha256);
            return new ElevatedRun(exit, StepOutcomes.FromWin32("fix-apply", 0, "run in this elevated process; no second prompt"));
        }, ct);
    }
}

internal static class FixPlanRoot
{
    // A new folder under the machine folder that only SYSTEM and Administrators can reach, for the plan of this run. The machine
    // folder itself must pass the same check the gate makes of it. Fails, with a step, rather than fall back to a folder an
    // ordinary program could write.
    public static StepOutcome CreateAdminOnly(IFolderSecurity folders, string machineFolder, Guid id, out string root)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFolder);
        root = Path.Combine(machineFolder, "fix-" + id.ToString("N"));
        StepOutcome made = folders.CreateHardened(machineFolder);
        if (!made.Ok)
        {
            return made;
        }

        StepOutcome read = folders.ReadSddl(machineFolder, out string? sddl);
        if (!read.Ok)
        {
            return read;
        }

        IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(sddl);
        if (problems.Count > 0)
        {
            return StepOutcomes.NotAttempted("fix-plan-folder", "The machine folder can be changed by more than administrators, so no plan is written there: " + problems[0]);
        }

        return folders.CreateWithSddl(root, UpdateActions.WorkFolderSddl);
    }
}
