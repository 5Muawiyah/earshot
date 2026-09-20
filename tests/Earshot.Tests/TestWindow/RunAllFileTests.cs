using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// test-gui.md section 11: "run-all.json holds the order, the item it stopped at, and for each
// item a pointer to its run folder. No outcome is stored." T14's "a corrupt run-all.json changes
// no row": every failure path here returns null from this file alone, never touching
// EvidenceStore/StateDeriver's own reads.
[TestClass]
public sealed class RunAllFileTests
{
    private string _liveTestRoot = null!;

    [TestInitialize]
    public void CreateScratchLiveTestRoot()
    {
        _liveTestRoot = Path.Combine(Path.GetTempPath(), "earshot-runallfile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_liveTestRoot);
    }

    [TestCleanup]
    public void DeleteScratchLiveTestRoot()
    {
        if (Directory.Exists(_liveTestRoot))
        {
            Directory.Delete(_liveTestRoot, recursive: true);
        }
    }

    [TestMethod]
    public void AMissingFileReadsAsNullNeverAsAnEmptyRecord()
    {
        Assert.IsNull(RunAllFile.TryRead(_liveTestRoot));
    }

    [TestMethod]
    public void WriteThenReadRoundTripsTheOrderTheStopIndexAndThePointers()
    {
        var record = new RunAllRecord
        {
            Order = new[] { "01", "02", "10v1" },
            StoppedAtIndex = 1,
            Pointers = new Dictionary<string, string> { ["01"] = "20260920T000000Z", ["02"] = "20260920T000100Z" },
        };

        RunAllFile.Write(_liveTestRoot, record);
        RunAllRecord? read = RunAllFile.TryRead(_liveTestRoot);

        Assert.IsNotNull(read);
        CollectionAssert.AreEqual((System.Collections.ICollection)record.Order, (System.Collections.ICollection)read!.Order);
        Assert.AreEqual(1, read.StoppedAtIndex);
        Assert.AreEqual("20260920T000000Z", read.Pointers["01"]);
        Assert.AreEqual("20260920T000100Z", read.Pointers["02"]);
    }

    [TestMethod]
    public void ANewRecordsDefaultStoppedAtIndexIsMinusOne()
    {
        var record = new RunAllRecord { Order = new[] { "01" } };
        Assert.AreEqual(-1, record.StoppedAtIndex);
    }

    [TestMethod]
    public void GarbageTextReadsAsNull()
    {
        File.WriteAllText(Path.Combine(_liveTestRoot, RunAllFile.FileName), "not json at all");
        Assert.IsNull(RunAllFile.TryRead(_liveTestRoot));
    }

    [TestMethod]
    public void AJsonArrayInsteadOfAnObjectReadsAsNull()
    {
        File.WriteAllText(Path.Combine(_liveTestRoot, RunAllFile.FileName), "[1,2,3]");
        Assert.IsNull(RunAllFile.TryRead(_liveTestRoot));
    }

    [TestMethod]
    public void AMissingOrderMemberReadsAsNull()
    {
        File.WriteAllText(Path.Combine(_liveTestRoot, RunAllFile.FileName), """{"stoppedAtIndex":0}""");
        Assert.IsNull(RunAllFile.TryRead(_liveTestRoot));
    }

    [TestMethod]
    public void ANonStringEntryInOrderReadsAsNull()
    {
        File.WriteAllText(Path.Combine(_liveTestRoot, RunAllFile.FileName), """{"order":["01", 2]}""");
        Assert.IsNull(RunAllFile.TryRead(_liveTestRoot));
    }

    private static readonly string[] SingleItemOrder = { "01" };

    [TestMethod]
    public void DeleteRemovesTheFileAndIsSafeWhenNothingIsThere()
    {
        RunAllFile.Write(_liveTestRoot, new RunAllRecord { Order = SingleItemOrder });
        Assert.IsTrue(File.Exists(Path.Combine(_liveTestRoot, RunAllFile.FileName)));

        RunAllFile.Delete(_liveTestRoot);
        Assert.IsFalse(File.Exists(Path.Combine(_liveTestRoot, RunAllFile.FileName)));

        RunAllFile.Delete(_liveTestRoot);
    }

    [TestMethod]
    public void TheKeyForANonVariantItemIsItsRowNumberAlone()
    {
        Assert.AreEqual("08", RunAllFile.Key(new RunAllItem("08", null)));
    }

    [TestMethod]
    public void TheKeyForAVariantItemAppendsVTheVariantNumber()
    {
        Assert.AreEqual("10v3", RunAllFile.Key(new RunAllItem("10", 3)));
    }
}
