using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Boot.Gate;

// update <zipPath> <sha256> <trayPid> <userSid> <address> <containerGuid>, run elevated from the tray with one UAC
// prompt. The request is already validated.
internal sealed record UpdateRequest(string ZipPath, string ZipSha256, int TrayProcessId, InstallRequest Install);

// Waits for a process to end. The tray closes itself for an update, and the install replaces the folder the tray runs
// from, so the update waits for it. Ok when the process has ended or was never running.
internal interface IProcessWaiter
{
    // image: the program the process id must belong to. An id that now belongs to another program (the tray ended and
    // its id was reused) is not waited on.
    StepOutcome WaitForExit(int processId, string image, TimeSpan timeout);
}

// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexit
internal sealed class ProcessExitWaiter : IProcessWaiter
{
    public const string Step = "wait-tray";

    public StepOutcome WaitForExit(int processId, string image, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws ArgumentException for an id no process has.
            return new StepOutcome(Step, true, 0, "S_OK", "Process " + processId.ToString(CultureInfo.InvariantCulture) + " is not running.");
        }

        using (process)
        {
            try
            {
                // The image is read from the process itself. The module list Process.MainModule reads from can still
                // be empty, or hold ntdll.dll first, for a process that has only just started.
                string? running = ProcessImageName.TryRead(process.SafeHandle, out uint error);
                if (running is null)
                {
                    // Not known: the process is waited for, which errs on the side of not touching the install folder.
                    Trace.WriteLine("Earshot update: the image of process " + processId.ToString(CultureInfo.InvariantCulture) + " could not be read (Win32 error " + error.ToString(CultureInfo.InvariantCulture) + "), so it is waited for.");
                }
                else if (!string.Equals(Path.GetFullPath(running), Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase))
                {
                    return new StepOutcome(Step, true, 0, "S_OK",
                        "Process " + processId.ToString(CultureInfo.InvariantCulture) + " is " + running + ", not " + image + ", so the tray has ended.");
                }

                return process.WaitForExit(timeout)
                    ? new StepOutcome(Step, true, 0, "S_OK", "Process " + processId.ToString(CultureInfo.InvariantCulture) + " ended.")
                    : StepOutcomes.NotAttempted(Step,
                        "Process " + processId.ToString(CultureInfo.InvariantCulture) + " was still running after " + timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.");
            }
            catch (Win32Exception ex)
            {
                // The wait itself failed: the tray is not known to have ended.
                return StepOutcomes.FromWin32(Step, unchecked((uint)ex.NativeErrorCode), "Process " + processId.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message, ok: false);
            }
            catch (InvalidOperationException)
            {
                // The process ended while it was being looked at.
                return new StepOutcome(Step, true, 0, "S_OK", "Process " + processId.ToString(CultureInfo.InvariantCulture) + " ended.");
            }
        }
    }
}

// The full path of the program a process is running, from the process itself.
// https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew
internal static unsafe partial class ProcessImageName
{
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint size);

    // Null with the Win32 error when the path cannot be read.
    internal static string? TryRead(SafeProcessHandle process, out uint error)
    {
        char[] buffer = new char[1024];
        uint size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            if (!QueryFullProcessImageName(process, 0, p, ref size))
            {
                error = unchecked((uint)Marshal.GetLastPInvokeError());
                return null;
            }
        }

        error = 0;
        return new string(buffer, 0, (int)size);
    }
}

// Starts the unpacked, checked release's own Earshot.exe with the install verb. The elevated update run is an
// elevated process, and a process it starts without a verb has the same elevated token, so no second prompt.
internal interface IInstallStarter
{
    StepOutcome Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute
internal sealed class ChildInstallStarter : IInstallStarter
{
    public const string Step = "update-start-install";

    public StepOutcome Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(info);
            return process is null
                ? StepOutcomes.NotAttempted(Step, "No process was started for " + executable + ".")
                : new StepOutcome(Step, true, 0, "S_OK", executable + " " + string.Join(' ', arguments.Take(1)) + " started as process " + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
        }
        catch (Win32Exception ex)
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)ex.NativeErrorCode), executable + ": " + ex.Message, ok: false);
        }
        catch (InvalidOperationException ex)
        {
            return StepOutcomes.FromHResult(Step, ex.HResult, executable + ": " + ex.Message, ok: false);
        }
    }
}

// The elevated half of an update, run by the installed, administrator-owned Earshot.exe. It never trusts anything the
// tray, or any program the signed-in user runs, left in a folder that user can write:
//   1. it must be running from the install folder, and that folder must grant no one but administrators write;
//   2. earlier update work folders are removed (each only after its security reads back as administrators-only);
//   3. a work folder is created beside the install folder with a security descriptor that gives only SYSTEM and
//      Administrators any access, read back and checked, and not a reparse point;
//   4. the zip is copied there through one read-only handle, the copy is hashed, and the update goes on only if the
//      hash is the one on the command line (the one the tray verified against the release's checksum file);
//   5. the copy is unpacked there with the same guards the tray applies (plain relative names, no repeats, size caps,
//      every file against the release's file list);
//   6. it waits for the tray to end, so nothing runs from the install folder but this program;
//   7. it starts the unpacked release's own Earshot.exe with the install verb and ends. That program runs from the
//      work folder, which only administrators can change, so what it copies into the install folder is what steps 4
//      and 5 checked. It is the release's own program, not this one, because an update must run the new version's setup
//      (a release may register or configure something the installed version does not know how to), and because a
//      process that renames the folder it is running from can fail to load a file it had not loaded yet. It is started
//      without a verb by an elevated process, so it is elevated with no second prompt. This program ends at once, and
//      install retries the folder move for a few seconds (InstallActions.FolderMoveAttempts) in case it has not yet.
// The install runs from the work folder and no program is left to remove it when the install ends, so the next update
// run removes it.
//
// The tray has exited before any of this ends, so how it ended is written to update-outcome.json in the machine folder
// (UpdateOutcomeRecorder): this run records that it handed over, or why it stopped, and the install it started records
// that it finished or did not. The tray reads it once at its next start and says so on a card. Nothing here starts the
// tray again: a program started from this elevated one would be elevated too, and the tray must not run elevated. So the
// tray comes back at the owner's next sign-in (Open on startup) or when they start it, and the card is how it tells them.
internal sealed class UpdateActions
{
    // Waiting for the tray to end. Exit waits up to TrayContext.DefaultExitWaitLimit for actions in flight, keeps a
    // closing notice up for TrayContext.DefaultExitNoticeTime, and the hand-back inside it is capped at 4 s, so the
    // longest ordinary exit is a little over 34 s; 10 s more is margin. An exit that takes longer (the coordinator
    // is busy for minutes) makes the update refuse and change nothing, which the person can simply try again. A
    // waiting budget chosen here, not a measured figure.
    public static readonly TimeSpan TrayExitWait = TimeSpan.FromSeconds(45);

    // Beside the install folder: <install folder name>.update-<guid>.
    public const string WorkFolderTag = ".update-";

    // The security of the work folder: protected, owner Administrators, SYSTEM and Administrators full control and
    // nobody else.
    public const string WorkFolderSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private readonly InstallLayout _layout;
    private readonly IFolderSecurity _folders;
    private readonly IProcessWaiter _waiter;
    private readonly IInstallStarter _starter;
    private readonly ILog _log;

    public UpdateActions(InstallLayout layout, IFolderSecurity folders, IProcessWaiter waiter, IInstallStarter starter, ILog log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(log);
        _layout = layout;
        _folders = folders;
        _waiter = waiter;
        _starter = starter;
        _log = log;
    }

    // The wait for the tray. Tests shorten it.
    internal TimeSpan TrayWait { get; init; } = TrayExitWait;

    public InstallResult Run(UpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = new List<StepOutcome>();
        try
        {
            return RunSteps(request, steps);
        }
        catch (Exception ex)
        {
            steps.Add(ElevatedFailure.Step("update", ex));
            _log.Error("update stopped with " + ex.GetType().Name + " after " + steps.Count + " steps.", ex);
            return new InstallResult(GateExitCode.Failed, steps);
        }
    }

    private InstallResult RunSteps(UpdateRequest request, List<StepOutcome> steps)
    {
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.InstallFolder));
        string running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_layout.SourceFolder));
        if (!string.Equals(running, install, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(StepOutcomes.NotAttempted("update-running-from",
                "The update runs only from the installed copy, " + install + ", and this program is in " + running + "."));
            return new InstallResult(GateExitCode.NotFromInstallFolder, steps);
        }

        steps.Add(new StepOutcome("update-running-from", true, 0, "S_OK", install));
        if (!FolderTrust.IsTrusted(_folders, install, AclCheck.CheckInstallFolder, "install-folder-acl", steps))
        {
            return new InstallResult(GateExitCode.FolderNotSecure, steps);
        }

        RemoveEarlierWorkFolders(install, steps);

        string work = WorkFolderPath(install, Guid.NewGuid().ToString("N"));
        if (!CreateWorkFolder(work, steps))
        {
            FileSteps.DeleteTree(work, "remove-update-work", steps);
            return new InstallResult(GateExitCode.FolderNotSecure, steps);
        }

        GateExitCode copied = CopyAndVerifyZip(request, work, steps, out string zipCopy);
        if (copied != GateExitCode.Success)
        {
            FileSteps.DeleteTree(work, "remove-update-work", steps);
            return new InstallResult(copied, steps);
        }

        if (!Unpack(zipCopy, work, steps))
        {
            FileSteps.DeleteTree(work, "remove-update-work", steps);
            return new InstallResult(GateExitCode.Failed, steps);
        }

        // Nothing has touched the install folder yet. It is only touched once the tray has ended.
        StepOutcome waited = _waiter.WaitForExit(request.TrayProcessId, Path.Combine(install, TaskPlan.ExecutableName), TrayWait);
        steps.Add(waited);
        if (!waited.Ok)
        {
            steps.Add(StepOutcomes.NotAttempted("update-tray-still-running",
                "Earshot did not close in time, so nothing was changed. Try the update again."));
            FileSteps.DeleteTree(work, "remove-update-work", steps);
            return new InstallResult(GateExitCode.Failed, steps);
        }

        string app = Path.Combine(work, ReleaseArchive.AppFolderName);
        StepOutcome started = _starter.Start(
            Path.Combine(app, TaskPlan.ExecutableName),
            UpdateHandover.InstallArguments(new HandoverIdentity(request.Install.UserSid, request.Install.Address, request.Install.ContainerId)),
            app);
        steps.Add(started);
        if (!started.Ok)
        {
            FileSteps.DeleteTree(work, "remove-update-work", steps);
            return new InstallResult(GateExitCode.Failed, steps);
        }

        _log.Info("update: the checked release is installing from " + app + ".");
        return new InstallResult(GateExitCode.Success, steps);
    }

    private static string WorkFolderPath(string install, string suffix) =>
        Path.Combine(Path.GetDirectoryName(install) ?? install, Path.GetFileName(install) + WorkFolderTag + suffix);

    // Work folders an earlier update left. Each is deleted only when its security reads back as administrators-only,
    // so a folder someone else made with that name is never deleted from this elevated process. One that cannot be
    // removed (an install from it may still be running) stays for the next update.
    private void RemoveEarlierWorkFolders(string install, List<StepOutcome> steps)
    {
        string parent = Path.GetDirectoryName(install) ?? install;
        string[] earlier;
        try
        {
            earlier = Directory.GetDirectories(parent, Path.GetFileName(install) + WorkFolderTag + "*");
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-earlier-work", ex.HResult, parent + ": " + ex.Message, ok: true));
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-earlier-work", ex.HResult, parent + ": " + ex.Message, ok: true));
            return;
        }

        foreach (string folder in earlier)
        {
            var trust = new List<StepOutcome>();
            if (FolderTrust.IsTrusted(_folders, folder, CheckWorkFolder, "earlier-update-work-acl", trust))
            {
                steps.AddRange(trust);
                FileSteps.DeleteTree(folder, "remove-earlier-update-work", steps);
            }
            else
            {
                // A folder that fails its check does not stop this update: it is only left where it is.
                steps.AddRange(trust);
                steps.Add(StepOutcomes.NotAttempted("remove-earlier-update-work", folder + " did not pass its check, so it was left. Remove it by hand."));
            }
        }
    }

    // Creates the folder with the work folder's descriptor, reads it back and requires exactly that, and refuses a
    // reparse point (ReadSddl does). Nothing is put in a folder that fails.
    private bool CreateWorkFolder(string work, List<StepOutcome> steps)
    {
        StepOutcome created = _folders.CreateWithSddl(work, WorkFolderSddl);
        steps.Add(created);
        return created.Ok && FolderTrust.IsTrusted(_folders, work, CheckWorkFolder, "update-work-acl", steps);
    }

    // The shared rules for a folder only administrators may change (owner, protected, no write-like right for anyone
    // else), and then no access at all for anyone but SYSTEM and Administrators.
    internal static IReadOnlyList<string> CheckWorkFolder(string? sddl)
    {
        var problems = new List<string>(AclCheck.CheckMachineFolder(sddl));
        if (problems.Count > 0 || sddl is null)
        {
            return problems;
        }

        var system = new SecurityIdentifier(Sddl.LocalSystemSid);
        var administrators = new SecurityIdentifier(Sddl.AdministratorsSid);
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.DiscretionaryAcl is null)
        {
            problems.Add("There is no DACL, which grants everyone full access.");
            return problems;
        }

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is QualifiedAce { AceQualifier: AceQualifier.AccessAllowed } allowed &&
                allowed.SecurityIdentifier != system && allowed.SecurityIdentifier != administrators)
            {
                problems.Add("Access is allowed for " + allowed.SecurityIdentifier.Value + ", not only SYSTEM and Administrators.");
            }
        }

        return problems;
    }

    // Copies the zip through one read-only handle into the work folder, hashes the copy, and compares. Anything the
    // user does to the original after this point changes nothing.
    private static GateExitCode CopyAndVerifyZip(UpdateRequest request, string work, List<StepOutcome> steps, out string zipCopy)
    {
        zipCopy = Path.Combine(work, UpdateService.ZipFileName);
        try
        {
            var info = new FileInfo(request.ZipPath);
            if (!info.Exists)
            {
                steps.Add(StepOutcomes.FromHResult("update-copy-zip", unchecked((int)0x80070002), "Missing: " + request.ZipPath, ok: false));
                return GateExitCode.Failed;
            }

            // Only the final component is checked: a folder earlier in the path may be a link, which is why the copy is
            // hashed and must match the hash on the command line.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                steps.Add(StepOutcomes.NotAttempted("update-copy-zip", "The zip is a link, not a file: " + request.ZipPath));
                return GateExitCode.Failed;
            }

            using (var source = new FileStream(request.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(zipCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.None))
            {
                if (source.Length > UpdateService.MaxZipBytes)
                {
                    steps.Add(StepOutcomes.NotAttempted("update-copy-zip", "The zip is " + source.Length.ToString(CultureInfo.InvariantCulture) +
                        " bytes, over the " + UpdateService.MaxZipBytes.ToString(CultureInfo.InvariantCulture) + " byte limit."));
                    return GateExitCode.Failed;
                }

                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > UpdateService.MaxZipBytes)
                    {
                        steps.Add(StepOutcomes.NotAttempted("update-copy-zip", "The zip passed the " + UpdateService.MaxZipBytes.ToString(CultureInfo.InvariantCulture) + " byte limit."));
                        return GateExitCode.Failed;
                    }

                    output.Write(buffer, 0, read);
                }

                output.Flush(flushToDisk: true);
            }

            string actual;
            using (var check = new FileStream(zipCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            {
                actual = Convert.ToHexString(SHA256.HashData(check));
            }

            if (!string.Equals(actual, request.ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(StepOutcomes.NotAttempted("update-verify-zip",
                    "The copy's SHA-256 is " + actual + " but the update was given " + request.ZipSha256.ToUpperInvariant() + ", so nothing was installed."));
                return GateExitCode.Failed;
            }

            steps.Add(new StepOutcome("update-verify-zip", true, 0, "S_OK", "The copy in " + work + " matches SHA-256 " + actual + "."));
            return GateExitCode.Success;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-copy-zip", ex.HResult, ex.Message, ok: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-copy-zip", ex.HResult, ex.Message, ok: false));
        }

        return GateExitCode.Failed;
    }

    private static bool Unpack(string zipCopy, string work, List<StepOutcome> steps)
    {
        try
        {
            using var zip = new FileStream(zipCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            string app = ReleaseArchive.Unpack(zip, work);
            steps.Add(new StepOutcome("update-unpack", true, 0, "S_OK", "Unpacked and checked against the release's file list in " + app + "."));
            return true;
        }
        catch (UpdateService.UpdateException ex)
        {
            steps.Add(StepOutcomes.NotAttempted("update-unpack", ex.Kind + ": " + ex.Message));
        }
        catch (InvalidDataException ex)
        {
            steps.Add(StepOutcomes.NotAttempted("update-unpack", "The zip is not a valid archive: " + ex.Message));
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-unpack", ex.HResult, ex.Message, ok: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("update-unpack", ex.HResult, ex.Message, ok: false));
        }

        return false;
    }
}
