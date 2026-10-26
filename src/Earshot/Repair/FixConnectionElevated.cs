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

    // Removes the run's plan folder when it ends. A folder that stays is logged: it holds only a plan that nothing reads
    // without its hash.
    public static void Remove(string root, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log.Warn("fix: the plan folder could not be removed (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("fix: the plan folder could not be removed (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
        }
    }
}

// What a run uses to hand its plan to the apply step and to run that step.
internal sealed record FixElevation(IFixPlanFiles PlanFiles, IFixElevator Elevator, bool InProcess, string? PlanRoot, StepOutcome? Refusal);

internal static class FixElevationChoice
{
    // The prompting apply step: the installed copy through the administrator prompt, or, with none, the one that says to set up
    // Earshot first. installedExe is null when %ProgramFiles%\Earshot\Earshot.exe is not there.
    public static IFixElevator Prompting(IElevatedLauncher launcher, string? installedExe)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        return installedExe is null ? new NotInstalledFixElevator() : new ShellRunasFixElevator(launcher, installedExe);
    }

    // From an elevated administrator the apply step runs in this process and the plan goes to a new admin-only folder; from
    // anyone else the ordinary pair is used (a plan in the user's folder, one administrator prompt). When the admin-only folder
    // cannot be made and checked, the run is refused with the step that failed, and nothing falls back to the ordinary folder.
    public static FixElevation Choose(
        IProcessToken token, IFolderSecurity folders, string machineFolder, Guid id, Func<string, string, int> apply,
        IFixPlanFiles ordinaryPlanFiles, IFixElevator promptElevator)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(ordinaryPlanFiles);
        ArgumentNullException.ThrowIfNull(promptElevator);

        // SYSTEM never runs Fix connection (nobody is at its screen), so it gets the ordinary pair and the same plan-only stop.
        if (!token.IsElevatedAdministrator || token.IsLocalSystem)
        {
            return new FixElevation(ordinaryPlanFiles, promptElevator, false, null, null);
        }

        StepOutcome made = FixPlanRoot.CreateAdminOnly(folders, machineFolder, id, out string root);
        if (!made.Ok)
        {
            return new FixElevation(ordinaryPlanFiles, new RefusingFixElevator(made), true, null, made);
        }

        return new FixElevation(new FixPlanFiles(root), new InProcessFixElevator(apply), true, root, null);
    }
}

// The apply step when Earshot is not installed. Only %ProgramFiles%\Earshot\Earshot.exe is ever raised: a copy run from Downloads
// or a build folder sits where a standard user can change it. A run with changes to make says to set up Earshot first before
// it asks anything (FixConnection), and this applies nothing.
internal sealed class NotInstalledFixElevator : IFixElevator
{
    public const string StepName = "installed-copy";

    public static StepOutcome Why => StepOutcomes.NotAttempted(StepName, "Earshot is not installed in Program Files, so there is no copy to raise.");

    public Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return Task.FromResult(new ElevatedRun(null, Why));
    }
}

// The apply step of a run whose admin-only plan folder could not be made. It applies nothing and says why.
internal sealed class RefusingFixElevator : IFixElevator
{
    private readonly StepOutcome _why;

    public RefusingFixElevator(StepOutcome why)
    {
        ArgumentNullException.ThrowIfNull(why);
        _why = why;
    }

    public Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return Task.FromResult(new ElevatedRun(null, _why));
    }
}
