using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Update;

// How a repair goes about it.
internal enum RepairRoute
{
    // The running copy's own setup puts a new install in place. For when there is no installed program to run, or this
    // copy is newer than the installed one and its setup brings the install up to date.
    SetUpFromThisCopy,

    // The installed program repairs itself with one administrator prompt: its files all match what was published.
    InstalledProgram,

    // Some installed file is missing or is not what was published, so the install folder is not trusted: the release of
    // the installed version is downloaded and checked, and the installed program's update verb installs it.
    DownloadThenUpdate,
}

// Why is for the log. Verb is the elevated command line for the first and second routes; Version is the installed version,
// for the third.
internal sealed record RepairPlan(RepairRoute Route, RepairVerb Verb, ReleaseVersion? Version, string Why);

// The choice between the three routes, from facts the tray has read without changing anything.
internal static class RepairPlanner
{
    // A version at or above which Earshot.exe is known to accept the repair verb: the release that adds it is at least this
    // number. An installed program older than this is asked for the same repair through the install verb, which every
    // version runs from its own folder as a repair, because a program that does not know a verb would end with a usage
    // error after the administrator prompt, for nothing. A number that is too high only costs the recorded outcome (the
    // install verb records none for a repair), never the repair, so it is raised, never lowered, to the release that
    // carries the verb.
    internal static readonly ReleaseVersion RepairVerbSince = new(1, 2, 1);

    // install: what is in Program Files. runningCopyIsNewer: this copy carries a newer file version than the installed one.
    // files: every installed file against the published list, or null when it was not checked. installedVersion: the file
    // version of the installed Earshot.exe, or null when it could not be read.
    public static RepairPlan Decide(InstallState install, bool runningCopyIsNewer, InstalledFilesReport? files, Version? installedVersion)
    {
        if (install != InstallState.Usable)
        {
            return new RepairPlan(RepairRoute.SetUpFromThisCopy, RepairVerb.FromThisCopy, null,
                install == InstallState.Nothing ? "nothing is installed" : "the installed program is missing or its folder cannot be trusted");
        }

        if (runningCopyIsNewer)
        {
            return new RepairPlan(RepairRoute.SetUpFromThisCopy, RepairVerb.FromThisCopy, null,
                "this copy is newer than the installed one, so its setup brings the install up to date");
        }

        ReleaseVersion? version = installedVersion is { } v ? new ReleaseVersion(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)) : null;
        if (files is { Ok: true })
        {
            RepairVerb verb = version is { } known && known >= RepairVerbSince ? RepairVerb.Repair : RepairVerb.Install;
            return new RepairPlan(RepairRoute.InstalledProgram, verb, version, "every installed file matches what was published");
        }

        if (version is null)
        {
            return new RepairPlan(RepairRoute.SetUpFromThisCopy, RepairVerb.FromThisCopy, null,
                "an installed file does not match, and the installed version could not be read to fetch its release");
        }

        return new RepairPlan(RepairRoute.DownloadThenUpdate, RepairVerb.Install, version,
            files is null ? "the installed files were not checked" : files.Bad.Count + " installed file(s) are missing or do not match, or there is no usable file list");
    }
}
