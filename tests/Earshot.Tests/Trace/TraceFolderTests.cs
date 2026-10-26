using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// The elevated helper's folders. A standard user can make %ProgramData%\Earshot or its trace folder before Earshot does, and
// making a folder that is already there changes nothing about it, so each is read back and must pass the machine folder's check
// before anything is written below it, and no folder on the way may be a reparse point. Every refusal is logged.
[TestClass]
public sealed class TraceFolderTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    // Owned by the Users group, which may write: what a folder a standard user made first can look like.
    private const string UserMadeSddl = "O:BUG:BUD:(A;OICI;FA;;;BU)";

    private static TraceMachine Machine(TempFolder root, IFolderSecurity folders, CapturingLog log) =>
        new(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? root.Path : null), folders, log);

    [TestMethod]
    public void FoldersThatPassGiveANewRunFolderOnlyAdministratorsCanOpen()
    {
        using var root = new TempFolder();
        var folders = new FakeFolderSecurity();
        TraceMachine machine = Machine(root, folders, new CapturingLog());

        string? problem = machine.PrepareRunFolder(Nonce, out string run);

        Assert.IsNull(problem);
        Assert.AreEqual(Path.Combine(machine.TraceFolder, Nonce), run);
        Assert.AreEqual(TraceMachine.RunFolderSddl, folders.CreatedWith[run]);
    }

    [TestMethod]
    public void AMachineFolderAStandardUserCouldChangeIsRefusedAndNothingIsMadeBelowIt()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        var folders = new FakeFolderSecurity();
        TraceMachine machine = Machine(root, folders, log);
        string machineFolder = Path.GetDirectoryName(machine.TraceFolder)!;
        folders.SddlFor = path => string.Equals(path, machineFolder, StringComparison.OrdinalIgnoreCase) ? UserMadeSddl : null;

        string? problem = machine.PrepareRunFolder(Nonce, out string run);

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "trace-folder-acl");
        Assert.IsFalse(Directory.Exists(machine.TraceFolder), "Nothing is made inside a folder that failed.");
        Assert.IsFalse(Directory.Exists(run));
        Assert.IsTrue(log.Has(LogLevel.Warn, machineFolder));
    }

    [TestMethod]
    public void ATraceFolderAStandardUserMadeFirstIsRefusedAndNoRunFolderIsMadeInIt()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        var folders = new FakeFolderSecurity();
        TraceMachine machine = Machine(root, folders, log);
        folders.SddlFor = path => string.Equals(path, machine.TraceFolder, StringComparison.OrdinalIgnoreCase) ? UserMadeSddl : null;

        string? problem = machine.PrepareRunFolder(Nonce, out string run);

        Assert.IsNotNull(problem);
        Assert.IsFalse(Directory.Exists(run));
        Assert.IsTrue(log.Has(LogLevel.Warn, "trace-folder-acl"));
    }

    [TestMethod]
    public void AFolderWhoseSecurityCannotBeReadIsRefused()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        var folders = new FakeFolderSecurity { SddlFor = _ => "not a descriptor" };
        TraceMachine machine = Machine(root, folders, log);

        Assert.IsNotNull(machine.PrepareRunFolder(Nonce, out string run));
        Assert.IsFalse(Directory.Exists(run));
    }

    // A real junction, which a standard user can make without any privilege, where %ProgramData% would be.
    [TestMethod]
    public void AJunctionOnTheWayToTheMachineFolderIsRefusedAndNothingIsWrittenThroughIt()
    {
        using var root = new TempFolder();
        string elsewhere = Path.Combine(root.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var log = new CapturingLog();
        var folders = new FakeFolderSecurity();
        TraceMachine machine = Machine(root, folders, log);
        string programData = Path.GetDirectoryName(Path.GetDirectoryName(machine.TraceFolder)!)!;
        using IDisposable junction = TestLinks.CreateJunction(programData, elsewhere);

        Assert.AreEqual(programData, TraceMachine.FirstReparsePoint(machine.TraceFolder));
        string? problem = machine.PrepareRunFolder(Nonce, out _);

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "reparse point");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length, "Nothing was made through the junction.");
        Assert.AreEqual(0, folders.Created.Count);
        Assert.IsTrue(log.Has(LogLevel.Warn, "reparse point"));
    }

    [TestMethod]
    public void APathWithNoLinkOnItHasNoReparsePoint()
    {
        using var root = new TempFolder();

        Assert.IsNull(TraceMachine.FirstReparsePoint(Path.Combine(root.Path, "ProgramData", "Earshot", "trace")));
    }

    // The real descriptor of a folder made the ordinary way, as a standard user would make it: it inherits from its parent, so
    // the machine folder's check refuses it and the helper starts nothing there.
    [TestMethod]
    public void ARealFolderMadeTheOrdinaryWayFailsTheCheckAndIsRefused()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        TraceMachine machine = Machine(root, new NtfsFolderSecurity(), log);
        Directory.CreateDirectory(machine.TraceFolder);

        string? problem = machine.PrepareRunFolder(Nonce, out string run);

        Assert.IsNotNull(problem);
        Assert.IsFalse(Directory.Exists(run));
        Assert.IsTrue(log.Has(LogLevel.Warn, "trace-folder-acl"));
    }

    [TestMethod]
    public void AFailedDeleteIsLoggedAndReturned()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        TraceMachine machine = Machine(root, new FakeFolderSecurity(), log);
        string outside = Path.Combine(root.Path, "outside");
        Directory.CreateDirectory(outside);

        string? refused = machine.DeleteRunFolder(outside);

        Assert.IsNotNull(refused);
        Assert.IsTrue(Directory.Exists(outside));
        Assert.IsTrue(log.Has(LogLevel.Warn, "refused to delete"));
    }

    [TestMethod]
    public void AFileThatCannotBeReadIsLoggedWithItsCode()
    {
        using var root = new TempFolder();
        var log = new CapturingLog();
        TraceMachine machine = Machine(root, new FakeFolderSecurity(), log);

        Assert.IsNull(machine.ReadFile(Path.Combine(root.Path, "no-such-file.wprp")));
        Assert.IsTrue(log.Has(LogLevel.Warn, "HRESULT 0x"));
    }
}
