using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// Where the elevated modes write their logs. A run started from a user's session never writes a file under that
// user's profile: it logs to the checked machine folder, or to the debugger output only.
[TestClass]
public sealed class ElevatedLogTests
{
    private static Paths PathsUnder(TempFolder temp) =>
        Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);

    [TestMethod]
    public void OnlyTheTrayAndSystemWriteTheProfileLog()
    {
        using var temp = new TempFolder();
        Paths paths = PathsUnder(temp);

        ILog tray = Program.ModeLog(paths, privileged: false, isLocalSystem: () => throw new AssertFailedException("The tray never asks."));
        ILog system = Program.ModeLog(paths, privileged: true, isLocalSystem: () => true);
        ILog user = Program.ModeLog(paths, privileged: true, isLocalSystem: () => false);

        Assert.AreEqual(paths.LogFile, ((FileLog)tray).FilePath);
        Assert.AreEqual(paths.LogFile, ((FileLog)system).FilePath, "SYSTEM's own profile is not a user's.");
        Assert.IsInstanceOfType<DebugOutputLog>(user);

        user.Warn("Refused.");
        Assert.IsFalse(Directory.Exists(paths.LogFolder), "An elevated run started by a user wrote under the profile.");
    }

    [TestMethod]
    public void TheMachineLogIsUsedOnlyOnceTheMachineFolderPassesItsCheck()
    {
        using var temp = new TempFolder();
        Paths paths = PathsUnder(temp);
        var folders = new FakeFolderSecurity();

        Assert.IsNull(Program.MachineLog(paths, folders, out string missing));
        StringAssert.Contains(missing, "could not be checked");

        Directory.CreateDirectory(paths.MachineFolder);
        FileLog? trusted = Program.MachineLog(paths, folders, out string none);
        Assert.AreEqual(Path.Combine(paths.MachineFolder, "logs", "earshot.log"), trusted?.FilePath);
        Assert.AreEqual("", none);

        folders.DefaultMachineSddl = "O:BAG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";
        Assert.IsNull(Program.MachineLog(paths, folders, out string unsafeFolder));
        StringAssert.Contains(unsafeFolder, "did not pass its check");
    }

    [TestMethod]
    public void AHeldLogWritesNothingUntilItIsFlushedAndThenKeepsTheOrder()
    {
        using var temp = new TempFolder();
        var log = new HeldLog("install");

        log.Info("first");
        log.Warn("second", new InvalidOperationException("boom"));
        Assert.AreEqual(2, log.Count);
        Assert.IsEmpty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories), "A held log wrote a file before it was flushed.");

        var target = new FileLog(Path.Combine(temp.Path, "logs"));
        log.FlushTo(target, "");

        string[] lines = File.ReadAllLines(target.FilePath);
        Assert.IsTrue(lines[0].EndsWith("INFO first", StringComparison.Ordinal));
        Assert.IsTrue(lines[1].EndsWith("WARN second", StringComparison.Ordinal));
        Assert.IsTrue(lines[2].StartsWith("  System.InvalidOperationException: boom", StringComparison.Ordinal));
        Assert.AreEqual(0, log.Count);
    }

    [TestMethod]
    public void AHeldLogWithNoTargetWritesNoFile()
    {
        using var temp = new TempFolder();
        var log = new HeldLog("uninstall");
        log.Info("removed");

        log.FlushTo(null, "the machine folder could not be checked");

        Assert.AreEqual(0, log.Count);
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path));
    }

    [TestMethod]
    public void AHeldLogKeepsABoundedNumberOfEntriesAndSaysHowManyMore()
    {
        using var temp = new TempFolder();
        var log = new HeldLog("install");
        for (int i = 0; i < HeldLog.MaxEntries + 3; i++)
        {
            log.Info("entry");
        }

        Assert.AreEqual(HeldLog.MaxEntries, log.Count);
        var target = new FileLog(temp.Path);
        log.FlushTo(target, "");

        string[] lines = File.ReadAllLines(target.FilePath);
        Assert.HasCount(HeldLog.MaxEntries + 1, lines);
        StringAssert.Contains(lines[^1], "3 more entries went to the debugger output only");
    }
}
