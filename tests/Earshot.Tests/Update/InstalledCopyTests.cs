using System.Diagnostics;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// What the tray can tell about the install before it hands anything to it, and the two helpers that touch Windows for
// it: the read of a folder's real security, and the plain start of the installed program. Each helper that a test
// fakes somewhere else also has an execution of its own here, because a fake at a boundary proves everything except the
// boundary. Nothing here elevates, installs or touches Program Files.
[TestClass]
public sealed class InstalledCopyTests
{
    private const string Exe = @"C:\Program Files\Earshot\Earshot.exe";
    private const string Folder = @"C:\Program Files\Earshot";

    private static InstallAssessment Assess(bool program, bool folder, string? sddl, bool unreadable = false, Func<string, IEnumerable<string>>? list = null)
    {
        IFolderSecurity security = unreadable ? new UnreadableFolderSecurity() : new FixedFolderSecurity(sddl ?? FixedFolderSecurity.AdministratorsOnly);

        // The listing is faked here so no test reads the owner's Program Files: a folder that lists without the program.
        return InstalledCopy.Assess(Exe, path => program && path == Exe, path => folder && path == Folder, security, list ?? (_ => [Path.Combine(Folder, "Earshot.dll")]));
    }

    [TestMethod]
    public void NoFolderAndNoProgramIsNothingInstalled()
    {
        Assert.AreEqual(InstallState.Nothing, Assess(program: false, folder: false, sddl: null).State);
        Assert.AreEqual(InstallState.Nothing, InstalledCopy.Assess(null, _ => true, _ => true, new FixedFolderSecurity(FixedFolderSecurity.AdministratorsOnly)).State);
    }

    [TestMethod]
    public void AFolderWithNoProgramIsAnInstallThatCannotBeUsed()
    {
        InstallAssessment assessment = Assess(program: false, folder: true, sddl: null);

        Assert.AreEqual(InstallState.Unusable, assessment.State);
        Assert.AreEqual(InstallProblem.ProgramAbsent, assessment.Problem, "The folder lists without the program: it is truly absent.");
        StringAssert.Contains(assessment.Detail, "missing");
    }

    // File.Exists is false for a file it could not look at as well as for one that is not there, so only a folder that lists
    // without the file shows it is gone. A listing that fails, or that does list the file, is not that.
    [TestMethod]
    public void AProgramThatWasNotFoundIsAbsentOnlyWhenTheFolderListsWithoutIt()
    {
        InstallAssessment refused = Assess(program: false, folder: true, sddl: null, list: _ => throw new UnauthorizedAccessException("denied"));
        Assert.AreEqual(InstallState.Unusable, refused.State);
        Assert.AreEqual(InstallProblem.ProgramNotConfirmedAbsent, refused.Problem);
        StringAssert.Contains(refused.Detail, "could not be listed (0x80070005)", "The raw code of the failed listing is kept.");

        InstallAssessment io = Assess(program: false, folder: true, sddl: null, list: _ => throw new IOException("in use", unchecked((int)0x80070020)));
        Assert.AreEqual(InstallProblem.ProgramNotConfirmedAbsent, io.Problem);
        StringAssert.Contains(io.Detail, "0x80070020");

        InstallAssessment listed = Assess(program: false, folder: true, sddl: null, list: _ => [Path.Combine(Folder, "earshot.EXE")]);
        Assert.AreEqual(InstallProblem.ProgramNotConfirmedAbsent, listed.Problem, "A folder that lists the file is not evidence that it is gone.");
    }

    [TestMethod]
    public void TheRealListingOfAFolderWithoutTheProgramConfirmsItAbsentAndAFailedListingDoesNot()
    {
        using var temp = new TempFolder();
        string folder = Directory.CreateDirectory(Path.Combine(temp.Path, "Earshot")).FullName;
        File.WriteAllText(Path.Combine(folder, "Earshot.dll"), "library");
        string exe = Path.Combine(folder, "Earshot.exe");

        InstallAssessment absent = InstalledCopy.Assess(exe, File.Exists, Directory.Exists, new NtfsFolderSecurity());
        Assert.AreEqual(InstallProblem.ProgramAbsent, absent.Problem, absent.Detail);

        // A folder that vanishes between the two reads cannot be listed: not confirmed.
        InstallAssessment vanished = InstalledCopy.Assess(exe, _ => false, _ => true, new NtfsFolderSecurity(), _ => throw new DirectoryNotFoundException(folder));
        Assert.AreEqual(InstallProblem.ProgramNotConfirmedAbsent, vanished.Problem);
    }

    // The program another program holds open with no sharing is still found: File.Exists reads attributes, which a lock
    // does not stop, so a standard user cannot make an installed program look missing by holding it.
    [TestMethod]
    public void AProgramHeldOpenWithNoSharingIsStillFound()
    {
        using var temp = new TempFolder();
        string exe = Path.Combine(temp.Path, "Earshot.exe");
        File.WriteAllText(exe, "program");
        using var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.IsTrue(File.Exists(exe), "A locked file still exists.");
        InstallAssessment assessment = InstalledCopy.Assess(exe, File.Exists, Directory.Exists, new NtfsFolderSecurity());
        Assert.AreNotEqual(InstallProblem.ProgramAbsent, assessment.Problem, assessment.Detail);
        Assert.AreNotEqual(InstallProblem.ProgramNotConfirmedAbsent, assessment.Problem, assessment.Detail);
    }

    [TestMethod]
    public void AProgramInAFolderOnlyAdministratorsCanWriteIsUsable()
    {
        Assert.AreEqual(InstallState.Usable, Assess(program: true, folder: true, sddl: null).State);
    }

    [TestMethod]
    public void AFolderAStandardUserCanWriteIsNotUsableAndNeitherIsOneWhoseSecurityCannotBeRead()
    {
        InstallAssessment writable = Assess(program: true, folder: true, FixedFolderSecurity.UsersCanWrite);
        Assert.AreEqual(InstallState.Unusable, writable.State);
        Assert.AreEqual(InstallProblem.FolderNotTrusted, writable.Problem);
        StringAssert.Contains(writable.Detail, "not trusted");

        InstallAssessment unreadable = Assess(program: true, folder: true, sddl: null, unreadable: true);
        Assert.AreEqual(InstallState.Unusable, unreadable.State, "A folder whose security cannot be read is never trusted.");
        StringAssert.Contains(unreadable.Detail, "E_ACCESSDENIED");
        Assert.AreEqual(InstallProblem.FolderNotRead, unreadable.Problem, "A failed read is its own finding, not a folder that was found writable.");
        Assert.AreNotEqual(InstallProblem.FolderNotTrusted, unreadable.Problem);
    }

    private sealed class UnreadableFolderSecurity : IFolderSecurity
    {
        public StepOutcome CreateHardened(string path) => throw new InvalidOperationException("Reads only.");

        public StepOutcome CreateWithSddl(string path, string sddl) => throw new InvalidOperationException("Reads only.");

        public StepOutcome ReadSddl(string path, out string? sddl)
        {
            sddl = null;
            return StepOutcomes.FromHResult("folder-acl-read", unchecked((int)0x80070005), path);
        }
    }

    [TestMethod]
    public void TwoPathsAreTheSameFileWhenWindowsWouldSayTheyAreAndNotWhenEitherIsMissing()
    {
        Assert.IsTrue(InstalledCopy.SameFile(@"C:\Program Files\Earshot\Earshot.exe", @"c:\program files\earshot\..\earshot\EARSHOT.EXE"));
        Assert.IsFalse(InstalledCopy.SameFile(Exe, UpdateTrayHarness.OtherExe));
        Assert.IsFalse(InstalledCopy.SameFile(Exe, null));
        Assert.IsFalse(InstalledCopy.SameFile(null, null));
    }

    // One rule for every place that asks whether this is the installed copy: the installed copy, the startup value and the
    // shortcut all use it, so none of them can take the same file for another one.
    [TestMethod]
    public void OnePathRuleServesTheSwitchTheStartupValueAndTheShortcut()
    {
        Assert.IsTrue(NotificationRegistration.PathsEqual(@"C:\Program Files\Earshot\Earshot.exe", "C:/Program Files/Earshot/Earshot.exe"));
        Assert.IsTrue(NotificationRegistration.PathsEqual(@"C:\Program Files\Earshot\Earshot.exe", @"c:\PROGRAM FILES\earshot\sub\..\Earshot.exe"));
        Assert.IsFalse(NotificationRegistration.PathsEqual(@"C:\Program Files\Earshot\Earshot.exe", @"C:\Program Files\Earshot\Other.exe"));
        Assert.IsFalse(NotificationRegistration.PathsEqual("", @"C:\Earshot.exe"));
        Assert.IsFalse(NotificationRegistration.PathsEqual("  ", "  "), "A blank path names nothing.");

        // A path Windows cannot name in full is compared as written, not thrown at the caller.
        string odd = "C:\\Earshot\0.exe";
        Assert.IsTrue(NotificationRegistration.PathsEqual(odd, odd.ToUpperInvariant()));
        Assert.IsFalse(NotificationRegistration.PathsEqual(odd, @"C:\Earshot.exe"));
    }

    // ----- the real folder security read, in places this run can see -----

    // A folder the running user made is owned by them and they can write it: the real read and the real rules call it
    // not trusted. This is the case the check exists for.
    [TestMethod]
    public void TheRealReadCallsAFolderTheUserMadeUnusable()
    {
        using var temp = new TempFolder();
        string folder = Directory.CreateDirectory(Path.Combine(temp.Path, "Earshot")).FullName;
        string exe = Path.Combine(folder, "Earshot.exe");
        File.WriteAllText(exe, "program");

        InstallAssessment assessment = InstalledCopy.Assess(exe, File.Exists, Directory.Exists, new NtfsFolderSecurity());

        Assert.AreEqual(InstallState.Unusable, assessment.State, assessment.Detail);
        StringAssert.Contains(assessment.Detail, "not trusted");
    }

    // Windows' own System32 is the folder only administrators and TrustedInstaller can change, on every machine: the
    // real read and the real rules call a program in it usable.
    [TestMethod]
    public void TheRealReadCallsAFolderOnlyAdministratorsCanWriteUsable()
    {
        string exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        InstallAssessment assessment = InstalledCopy.Assess(exe, File.Exists, Directory.Exists, new NtfsFolderSecurity());

        Assert.AreEqual(InstallState.Usable, assessment.State, assessment.Detail);
    }

    [TestMethod]
    public void TheRealAssessmentOfAFolderThatIsNotThereIsNothingInstalled()
    {
        using var temp = new TempFolder();

        InstallAssessment assessment = InstalledCopy.Assess(Path.Combine(temp.Path, "Earshot", "Earshot.exe"), File.Exists, Directory.Exists, new NtfsFolderSecurity());

        Assert.AreEqual(InstallState.Nothing, assessment.State);
    }

    // ----- the real start of the installed program -----

    [TestMethod]
    public void TheRealStartStartsAProgramInItsOwnFolderAndReportsOneThatIsNotThere()
    {
        var log = new CapturingLog();
        string hostname = Path.Combine(Environment.SystemDirectory, "hostname.exe");

        bool started = InstalledCopyStarter.Start(hostname, log, hidden: true);

        Assert.IsTrue(started, "hostname.exe did not start.");
        Assert.IsTrue(log.Has(LogLevel.Info, "Switch: started " + hostname));

        using var temp = new TempFolder();
        bool missing = InstalledCopyStarter.Start(Path.Combine(temp.Path, "missing.exe"), log, hidden: true);

        Assert.IsFalse(missing);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Win32 error 2"), "The raw code is on record (ERROR_FILE_NOT_FOUND).");
    }

    // The log says which it was: the switch the person chose, or Earshot starting again after a hand-over that ended without an
    // install (a declined prompt, say). Both start the same program; only the reason differs.
    [TestMethod]
    public void TheStartSaysWhetherItWasASwitchOrAStartAgain()
    {
        string hostname = Path.Combine(Environment.SystemDirectory, "hostname.exe");

        var restart = new CapturingLog();
        Assert.IsTrue(InstalledCopyStarter.Start(hostname, restart, hidden: true, StartAfterExitKind.Restart));
        Assert.IsTrue(restart.Has(LogLevel.Info, "Restart: started " + hostname), "A start again is logged as one.");
        Assert.IsFalse(restart.Entries.Any(e => e.Message.StartsWith("Switch", StringComparison.Ordinal)), "It was not a switch.");

        var sw = new CapturingLog();
        Assert.IsTrue(InstalledCopyStarter.Start(hostname, sw, hidden: true, StartAfterExitKind.Switch));
        Assert.IsTrue(sw.Has(LogLevel.Info, "Switch: started " + hostname));

        using var temp = new TempFolder();
        var failed = new CapturingLog();
        Assert.IsFalse(InstalledCopyStarter.Start(Path.Combine(temp.Path, "missing.exe"), failed, hidden: true, StartAfterExitKind.Restart));
        Assert.IsTrue(failed.Has(LogLevel.Warn, "Restart: "), "A failure names which start it was.");
        Assert.IsTrue(failed.Has(LogLevel.Warn, "Win32 error 2"));
    }

    // ----- the wait for the process that started the update -----

    // The update is started by whichever copy is running. A copy run from a download folder is not the installed
    // program by path, but it is still Earshot.exe, and the elevated run must wait for it to finish its exit before it
    // touches the install folder: its exit still uses the installed program's scheduled tasks.
    [TestMethod]
    public void TheRealWaitWaitsForACopyRunFromAnotherFolderButNotForAnotherProgram()
    {
        var waiter = new ProcessExitWaiter();
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var slow = new ProcessStartInfo(cmd, "/c ping -n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using Process stays = Process.Start(slow)!;
        try
        {
            // The image the update names is the installed program's path, which is a different folder from the running
            // copy's. The file name is what says it is the same program.
            string installedNamedLikeIt = Path.Combine(Path.GetTempPath(), "Elsewhere", "cmd.exe");
            StepOutcome waited = waiter.WaitForExit(stays.Id, installedNamedLikeIt, TimeSpan.FromMilliseconds(400));
            Assert.IsFalse(waited.Ok, "A process with the program's name in another folder is waited for.");
            StringAssert.Contains(waited.Detail, "still running");
            Assert.IsFalse(stays.HasExited);

            StepOutcome other = waiter.WaitForExit(stays.Id, Path.Combine(Path.GetTempPath(), "Earshot.exe"), TimeSpan.FromSeconds(30));
            Assert.IsTrue(other.Ok, "An id that now belongs to a program with another name is not the tray, so it is not waited on.");
        }
        finally
        {
            stays.Kill(entireProcessTree: true);
            stays.WaitForExit();
        }
    }
}
