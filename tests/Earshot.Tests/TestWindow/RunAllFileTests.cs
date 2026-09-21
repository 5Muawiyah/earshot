using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// run-all.json holds the order, the item it stopped at, and for each item a pointer to its run
// folder. No outcome is stored. A corrupt run-all.json must change no row: every failure path
// here returns null from this file alone, never touching EvidenceStore/StateDeriver's own reads.
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

    // The harness's own Copy-AppEvidence sweeps every *.json file at the root of livetest\ and
    // copies new ones into whichever test folder is currently running. run-all.json living there
    // risked being swept into a live test's own evidence by mistake, so the window keeps it in
    // its own livetest-gui\ folder beside, never inside, the harness's live test root. Driven
    // through the real MainForm (a declined row halts Run all immediately, which is what saves
    // run-all.json), not by calling RunAllFile directly with a folder chosen by the test.
    [TestMethod]
    public void RunAllJsonIsNeverWrittenAtTheRootOfTheHarnesssOwnLiveTestFolder()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260920T000000Z", "01-a2dp-oneshot", "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "inconclusive").Build());

        // An unrelated, already-settled pass with leftAtRest yes, so the at-rest banner is not
        // what stops Run all here: this test is about the per-row halt decision alone.
        ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260920T010000Z", "15-uninstall-reversal", "result.json"),
            new ResultJsonFixture("15-uninstall-reversal", "pass").WithCriterion("delayed-deletion", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-20T01:00:00.000Z").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();
            Assert.IsTrue(form.CarryOnVisibleForTests, "Run all did not halt on the declined row as expected.");
        });

        Assert.IsFalse(File.Exists(Path.Combine(liveTestRoot, RunAllFile.FileName)),
            "run-all.json must never be written at the root of the harness's own livetest folder.");
        Assert.IsTrue(File.Exists(Path.Combine(sandbox.Path, "local", "Earshot", "livetest-gui", RunAllFile.FileName)),
            "run-all.json was not found in the window's own livetest-gui folder.");
    }
}
