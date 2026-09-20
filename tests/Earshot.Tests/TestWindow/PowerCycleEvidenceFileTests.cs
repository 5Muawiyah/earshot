using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// gui-power-cycle.json's shape (test-gui.md section 9.3): a top-level "verdict" member with
// exactly the spellings EvidenceStore.TryReadPowerCycleVerdict reads back, plus the raw rows,
// additive and never overwriting anything the harness itself writes.
[TestClass]
public sealed class PowerCycleEvidenceFileTests
{
    private string _folder = null!;

    [TestInitialize]
    public void CreateScratchFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "earshot-power-cycle-evidence-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void DeleteScratchFolder()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // DataRow cannot carry PowerCycleVerdict itself (internal, and a [TestMethod] must be
    // public), so the case is named by the exact string EvidenceStore is expected to read back.
    [TestMethod]
    [DataRow("power-down")]
    [DataRow("restart")]
    [DataRow("not-yet")]
    [DataRow("unknown")]
    public void EveryVerdictWritesTheSpellingEvidenceStoreReadsBack(string expectedText)
    {
        PowerCycleVerdict verdict = expectedText switch
        {
            "power-down" => PowerCycleVerdict.PowerDown,
            "restart" => PowerCycleVerdict.Restart,
            "not-yet" => PowerCycleVerdict.NotYet,
            _ => PowerCycleVerdict.Unknown,
        };
        string raw = """{"kernelPower109":[],"kernelGeneral12":[],"kernelBoot27":[]}""";
        PowerCycleEvidenceFile.Write(_folder, raw, verdict);

        string path = Path.Combine(_folder, "gui-power-cycle.json");
        Assert.IsTrue(File.Exists(path));

        RunEvidence evidence = EvidenceStore.ReadRunEvidence("20260920T120000Z", _folder, "08-acceptance-power-cycle");
        Assert.AreEqual(expectedText, evidence.PowerCycleVerdict);
    }

    [TestMethod]
    public void AnUnparseableRawPayloadStillWritesAReadableVerdict()
    {
        PowerCycleEvidenceFile.Write(_folder, "not json at all", PowerCycleVerdict.Unknown);

        RunEvidence evidence = EvidenceStore.ReadRunEvidence("20260920T120000Z", _folder, "08-acceptance-power-cycle");
        Assert.AreEqual("unknown", evidence.PowerCycleVerdict);
    }
}
