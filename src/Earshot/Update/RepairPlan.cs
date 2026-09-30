using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Update;

// How a repair goes about it.
internal enum RepairRoute
{
    // The running copy's own setup puts a new install in place. Only for when there is no installed program to run: the
    // installed Earshot.exe is truly absent (its folder lists without it) or its folder can be written by a standard user.
    // The running copy may be in a folder the signed-in user can write, so it is never the elevated program while the
    // installed one can be.
    SetUpFromThisCopy,

    // The installed program repairs itself with one administrator prompt: its files all match what was published.
    InstalledProgram,

    // Some installed file is missing or is not what was published: the release of the installed version is downloaded and
    // checked, and the installed program's update verb installs it. The installed Earshot.exe runs that verb from the
    // install folder, which only administrators can change; what it installs is only the verified zip.
    DownloadThenUpdate,

    // This copy is newer than the installed one. It is not elevated; the verified update path (download, SHA-256, the
    // installed program's update verb) is what brings the install up to date.
    UpdateInstead,

    // A file, the file list or the installed version could not be read (another program holds it, or access was refused),
    // or the installed program could not be confirmed absent. That says nothing about what is installed, so nothing is
    // elevated: the person is told to try again.
    CouldNotRead,
}

// Why is for the log. Verb is the elevated command line for the first and second routes; Version is the installed version,
// for the third.
internal sealed record RepairPlan(RepairRoute Route, RepairVerb Verb, ReleaseVersion? Version, string Why);

// The choice between the routes, from facts the tray has read without changing anything.
internal static class RepairPlanner
{
    // A version at or above which Earshot.exe is known to accept the repair verb: the release that adds it is at least this
    // number. The first published 1.2.1 has no repair verb (it answers "Unknown command" after the administrator prompt),
    // and a build of 1.2.1 made later carries the same version number, so the release that has it is 1.2.2. An installed
    // program older than this is asked for the same repair through the install verb, which every version runs from its own
    // folder as a repair, because a program that does not know a verb would end with a usage error after the
    // administrator prompt, for nothing. A number that is too high only costs the recorded outcome (the install verb
    // records none for a repair), never the repair, so it is raised, never lowered, to the release that carries the verb.
    internal static readonly ReleaseVersion RepairVerbSince = new(1, 2, 2);

    // install: what is in Program Files. runningCopyIsNewer: this copy carries a newer file version than the installed one.
    // files: every installed file against the published list, or null when it was not checked. installedVersion: the file
    // version of the installed Earshot.exe, or null when it could not be read.
    public static RepairPlan Decide(InstallAssessment install, bool runningCopyIsNewer, InstalledFilesReport? files, Version? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.State == InstallState.Nothing)
        {
            return new RepairPlan(RepairRoute.SetUpFromThisCopy, RepairVerb.FromThisCopy, null, "nothing is installed");
        }

        if (install.State == InstallState.Unusable)
        {
            return install.Problem == InstallProblem.ProgramNotConfirmedAbsent
                ? new RepairPlan(RepairRoute.CouldNotRead, RepairVerb.Install, null, "the installed program could not be confirmed missing: " + install.Detail)
                : new RepairPlan(RepairRoute.SetUpFromThisCopy, RepairVerb.FromThisCopy, null,
                    install.Problem == InstallProblem.FolderNotTrusted
                        ? "the install folder can be written by more than administrators"
                        : "the installed program is missing");
        }

        if (runningCopyIsNewer)
        {
            return new RepairPlan(RepairRoute.UpdateInstead, RepairVerb.Install, null,
                "this copy is newer than the installed one, and only the verified update path brings the install up to date");
        }

        ReleaseVersion? version = installedVersion is { } v ? new ReleaseVersion(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)) : null;
        if (files is { SomethingCouldNotBeRead: true })
        {
            return new RepairPlan(RepairRoute.CouldNotRead, RepairVerb.Install, version, "could not be read: " + string.Join(", ", files.Unreadable));
        }

        if (files is { Ok: true })
        {
            RepairVerb verb = version is { } known && known >= RepairVerbSince ? RepairVerb.Repair : RepairVerb.Install;
            return new RepairPlan(RepairRoute.InstalledProgram, verb, version, "every installed file matches what was published");
        }

        if (version is null)
        {
            // The release to fetch is named by the installed version, which could not be read, so there is nothing to
            // fetch and nothing to decide from: not "missing".
            return new RepairPlan(RepairRoute.CouldNotRead, RepairVerb.Install, null, "an installed file does not match, and the installed version could not be read to fetch its release");
        }

        return new RepairPlan(RepairRoute.DownloadThenUpdate, RepairVerb.Install, version,
            files is null ? "the installed files were not checked" : files.Bad.Count + " installed file(s) are missing or do not match, or there is no usable file list");
    }
}
