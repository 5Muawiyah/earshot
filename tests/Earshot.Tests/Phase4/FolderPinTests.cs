using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// A status file is written only into a folder that was held open and checked immediately before the write. These run the
// real pinner against real folders and junctions in a temp tree: what is refused, and what a held folder cannot suffer.
[TestClass]
public sealed class FolderPinTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static GateStatusFile Status() =>
        new(GateStore.SchemaVersion, Nonce, GateVerbs.Preshutdown, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "success", 0, "Blocked",
            [new StepOutcome("preshutdown", true, 0, "S_OK", "already blocked")], StepsTruncated: false);

    private static readonly Func<string?, IReadOnlyList<string>> Passes = _ => [];

    private static WindowsFolderPinner Pinner(Func<string?, IReadOnlyList<string>>? check = null) => new(new NtfsFolderSecurity(), check ?? Passes);

    [TestMethod]
    public void AFolderThatIsHeldCannotBeRenamedDeletedOrReplaced()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Earshot");
        Directory.CreateDirectory(folder);

        IDisposable? pin = Pinner().Pin(folder, out StepOutcome step);

        Assert.IsNotNull(pin, step.Detail);
        Assert.IsTrue(step.Ok);
        Assert.ThrowsExactly<IOException>(() => Directory.Move(folder, Path.Combine(temp.Path, "moved")), "Renamed while held.");
        Assert.ThrowsExactly<IOException>(() => Directory.Delete(folder), "Deleted while held.");
        pin.Dispose();
        Directory.Move(folder, Path.Combine(temp.Path, "moved"));
        Assert.IsTrue(Directory.Exists(Path.Combine(temp.Path, "moved")), "Free again once the pin is let go.");
    }

    [TestMethod]
    public void AJunctionWhereTheFolderShouldBeIsRefusedAndNothingIsWrittenThroughIt()
    {
        using var temp = new TempFolder();
        string target = Path.Combine(temp.Path, "target");
        string link = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using IDisposable junction = TestLinks.CreateJunction(link, target);
        var store = new GateStore(link, Pinner());

        StepOutcome written = store.WriteStatus(Status());

        Assert.IsFalse(written.Ok);
        Assert.AreEqual("write-status", written.Step);
        StringAssert.Contains(written.Detail, "is a link or not a folder");
        Assert.IsEmpty(Directory.GetFileSystemEntries(target), "Nothing landed where the junction points.");
    }

    [TestMethod]
    public void AJunctionAboveTheFolderIsRefusedBecauseThePathLeadsElsewhere()
    {
        using var temp = new TempFolder();
        string realParent = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(realParent, "Earshot"));
        string linkedParent = Path.Combine(temp.Path, "ProgramData");
        using IDisposable junction = TestLinks.CreateJunction(linkedParent, realParent);
        string folder = Path.Combine(linkedParent, "Earshot");
        var store = new GateStore(folder, Pinner());

        StepOutcome written = store.WriteStatus(Status());

        Assert.IsFalse(written.Ok);
        StringAssert.Contains(written.Detail, "so nothing was written into it");
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(realParent, "Earshot")));
    }

    [TestMethod]
    public void AFolderThatFailsItsCheckAtTheMomentOfTheWriteIsNotWrittenInto()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Earshot");
        Directory.CreateDirectory(folder);
        var store = new GateStore(folder, Pinner(_ => ["Users can write to the folder."]));

        StepOutcome written = store.WriteStatus(Status());

        Assert.IsFalse(written.Ok);
        StringAssert.Contains(written.Detail, "Users can write to the folder.");
        Assert.IsEmpty(Directory.GetFileSystemEntries(folder));
    }

    // The real rule, on a folder the current user owns: not what a machine folder is, so it is refused. This is the real
    // check running against a real descriptor read from a held folder.
    [TestMethod]
    public void TheRealMachineFolderRuleRefusesAFolderTheCurrentUserOwns()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Earshot");
        Directory.CreateDirectory(folder);

        IDisposable? pin = new WindowsFolderPinner(new NtfsFolderSecurity(), AclCheck.CheckMachineFolder).Pin(folder, out StepOutcome step);

        Assert.IsNull(pin);
        Assert.IsFalse(step.Ok);
        StringAssert.Contains(step.Detail, "failed its check");
    }

    [TestMethod]
    public void AFolderThatIsThereAndPassesIsWrittenIntoAndTheFileIsRead()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Earshot");
        Directory.CreateDirectory(folder);
        var store = new GateStore(folder, Pinner());

        StepOutcome written = store.WriteStatus(Status());

        Assert.IsTrue(written.Ok, written.Detail);
        Assert.IsTrue(store.ReadStatus(Nonce).IsOk);
        Directory.Delete(folder, recursive: true);
    }

    [TestMethod]
    public void AFolderThatIsGoneIsNotCreatedAndNothingIsWritten()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "Earshot");
        var store = new GateStore(folder, Pinner());

        StepOutcome written = store.WriteStatus(Status());

        Assert.IsFalse(written.Ok);
        Assert.IsFalse(Directory.Exists(folder));
        Assert.IsTrue(written.Code != 0, "The raw code is kept: " + written.CodeName);
    }

    // The store the gate and the service use for the real machine folder is a pinning one.
    [TestMethod]
    public void TheStoreOfTheRealMachineFolderRefusesAJunction()
    {
        using var temp = new TempFolder();
        string target = Path.Combine(temp.Path, "target");
        string link = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using IDisposable junction = TestLinks.CreateJunction(link, target);

        StepOutcome written = GateActions.MachineStore(link).WriteStatus(Status());

        Assert.IsFalse(written.Ok);
        Assert.IsEmpty(Directory.GetFileSystemEntries(target));
    }
}