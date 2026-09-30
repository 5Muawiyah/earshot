using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Update;

// What there is in %ProgramFiles%\Earshot, as far as the tray can tell without asking for anything.
internal enum InstallState
{
    // No folder and no program: nothing is installed.
    Nothing,

    // The program is there and the folder grants no one but administrators write. The elevated update and repair runs
    // make the same folder check, so this is the install the tray may hand over to.
    Usable,

    // Something is installed but it cannot be handed over to: the program is missing, or the folder's permissions are
    // not as Earshot needs, or they could not be read.
    Unusable,
}

// Why an install is Unusable, for the choice of what to do about it. Detail is for the log: the raw reason, with the code of
// the read that failed.
internal enum InstallProblem
{
    None,

    // The folder lists and Earshot.exe is not in it: the program is truly absent. Only a person who can write to
    // Program Files can cause this, so it is the one case where the running copy's own setup may bring an install back.
    ProgramAbsent,

    // Earshot.exe could not be found, but the folder could not be listed to confirm it is gone, or it lists the file
    // that could not be found. That says nothing about what is installed, so nothing is elevated on the strength of it.
    ProgramNotConfirmedAbsent,

    // The program is there, but the folder's permissions are not administrators-only, or could not be read.
    FolderNotTrusted,
}

internal sealed record InstallAssessment(InstallState State, string Detail, InstallProblem Problem = InstallProblem.None);

// A read-only look at the installed copy. The elevated run that follows is the program in that folder and repeats the
// folder check itself (UpdateActions, InstallActions), so this only decides what the tray offers and whether there is
// anything to hand over to. Nothing is changed and nothing is started.
internal static class InstalledCopy
{
    // listFolder names the entries of a folder; it is asked only when the program was not found, to tell a program that is
    // gone from one that could not be seen. It is the real folder listing when not given.
    public static InstallAssessment Assess(
        string? installedExe, Func<string, bool> fileExists, Func<string, bool> folderExists, IFolderSecurity folders, Func<string, IEnumerable<string>>? listFolder = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(folderExists);
        ArgumentNullException.ThrowIfNull(folders);
        if (string.IsNullOrWhiteSpace(installedExe))
        {
            return new InstallAssessment(InstallState.Nothing, "No installed program path is known.");
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(installedExe)) ?? installedExe;
        bool program = fileExists(installedExe);
        if (!program && !folderExists(folder))
        {
            return new InstallAssessment(InstallState.Nothing, "Neither " + installedExe + " nor its folder is there.");
        }

        if (!program)
        {
            return AssessMissingProgram(installedExe, folder, listFolder ?? ListEntries);
        }

        var steps = new List<StepOutcome>();
        if (!FolderTrust.IsTrusted(folders, folder, AclCheck.CheckInstallFolder, "install-folder-acl", steps))
        {
            string why = string.Join(" ", steps.Where(s => !s.Ok).Select(s => s.Step + " " + s.CodeName + (string.IsNullOrEmpty(s.Detail) ? "" : ": " + s.Detail)));
            return new InstallAssessment(InstallState.Unusable, folder + " is not trusted. " + why, InstallProblem.FolderNotTrusted);
        }

        return new InstallAssessment(InstallState.Usable, installedExe);
    }

    // File.Exists is false for a file that is not there and for one that could not be looked at. Only a folder that lists
    // without the file shows it is gone, and the listing's own failure is kept with its code.
    private static InstallAssessment AssessMissingProgram(string installedExe, string folder, Func<string, IEnumerable<string>> listFolder)
    {
        string name = Path.GetFileName(installedExe);
        try
        {
            foreach (string entry in listFolder(folder))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                {
                    return new InstallAssessment(InstallState.Unusable, installedExe + " was not found, but " + folder + " lists it.", InstallProblem.ProgramNotConfirmedAbsent);
                }
            }
        }
        catch (IOException ex)
        {
            return new InstallAssessment(InstallState.Unusable, installedExe + " was not found and " + folder + " could not be listed (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message, InstallProblem.ProgramNotConfirmedAbsent);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new InstallAssessment(InstallState.Unusable, installedExe + " was not found and " + folder + " could not be listed (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message, InstallProblem.ProgramNotConfirmedAbsent);
        }

        return new InstallAssessment(InstallState.Unusable, installedExe + " is missing: " + folder + " lists without it.", InstallProblem.ProgramAbsent);
    }

    private static IEnumerable<string> ListEntries(string folder) => Directory.EnumerateFileSystemEntries(folder);

    // The same file, compared as Windows compares paths.
    public static bool SameFile(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

// Starts the installed Earshot.exe for the switch. Not elevated: it runs with this process's own token, which is the
// signed-in user's (the tray is never elevated). Called after the running copy has let go of the single-instance lock,
// because the installed copy would otherwise find the lock held and end at once.
internal static class InstalledCopyStarter
{
    // True when the process started. A failure is logged with its raw code and returned, never swallowed.
    // hidden is for tests, which start a console program and show no window.
    public static bool Start(string executable, ILog log, bool hidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(log);
        var info = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? "",
        };
        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                log.Warn("Switch: no process was started for " + executable + ".");
                return false;
            }

            log.Info("Switch: started " + executable + " as process " + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warn("Switch: " + executable + " did not start (Win32 error " + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            log.Warn("Switch: " + executable + " did not start (" + ex.GetType().Name + "): " + ex.Message);
            return false;
        }
    }
}
