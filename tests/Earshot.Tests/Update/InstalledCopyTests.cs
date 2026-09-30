using System.Diagnostics;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Earshot.Update;
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

    private static InstallAssessment Assess(bool program, bool folder, string? sddl, bool unreadable = false)
    {
        IFolderSecurity security = unreadable ? new UnreadableFolderSecurity() : new FixedFolderSecurity(sddl ?? FixedFolderSecurity.AdministratorsOnly);
        return InstalledCopy.Assess(Exe, path => program && path == Exe, path => folder && path == Folder, security);
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
        StringAssert.Contains(assessment.Detail, "missing");
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
        StringAssert.Contains(writable.Detail, "not trusted");

        InstallAssessment unreadable = Assess(program: true, folder: true, sddl: null, unreadable: true);
        Assert.AreEqual(InstallState.Unusable, unreadable.State, "A folder whose security cannot be read is never trusted.");
        StringAssert.Contains(unreadable.Detail, "E_ACCESSDENIED");
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
