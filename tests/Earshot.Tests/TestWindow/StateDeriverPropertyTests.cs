using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T3 (test-gui.md section 13): seeded random result.json files and folder layouts; for each,
// Derive() == Passed (green) implies an independently written predicate over the same files.
// The predicate is written from the same section 6.2 text as StateDeriver, but expressed the
// other way round (starting from "what must be true for a green pass" rather than walking the
// rules in order), so the two are unlikely to share the same mistake.
[TestClass]
public sealed class StateDeriverPropertyTests
{
    [TestMethod]
    public void AGreenPassAlwaysHasAGenuineSecondHalfWithAPassingSnapshotAndAConfirmedPowerDown()
    {
        var random = new Random(20260920);
        var spec = TestRowSpecFixtures.Test08();

        for (int i = 0; i < 200; i++)
        {
            using var root = new TempFolder();
            (bool wroteSnapshot, bool snapshotPasses, string verdict) = GenerateCase(random, root);

            DerivedRowState state = StateDeriver.Derive(spec, EvidenceStore.LoadEvidence(root.Path, spec.TestId), null, null);

            bool independentlyExpectedGreen = wroteSnapshot && snapshotPasses && verdict == "power-down";
            Assert.AreEqual(independentlyExpectedGreen, state.IsGreen,
                "Case " + i + ": snapshot written " + wroteSnapshot + ", snapshot passes " + snapshotPasses + ", verdict " + verdict);
        }
    }

    private static (bool WroteSnapshot, bool SnapshotPasses, string Verdict) GenerateCase(Random random, TempFolder root)
    {
        string stamp = "20260920T" + random.Next(0, 235959).ToString("000000", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        string folder = Path.Combine(root.Path, stamp, "08-acceptance-power-cycle");
        Directory.CreateDirectory(folder);

        string json = new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("ACCEPTANCE", "pass").Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);

        bool wroteSnapshot = random.Next(2) == 0;
        bool snapshotPasses = random.Next(2) == 0;
        if (wroteSnapshot)
        {
            string snapshot = snapshotPasses
                ? new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("default-config", "pass").WithCriterion("blocked-before-power-cycle", "pass").Build()
                : new ResultJsonFixture("08-acceptance-power-cycle", "fail").WithCriterion("default-config", "fail").Build();
            File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"), snapshot);
        }

        string[] verdicts = { "power-down", "restart", "not-yet", "unknown" };
        string verdict = verdicts[random.Next(verdicts.Length)];
        File.WriteAllText(Path.Combine(folder, "gui-power-cycle.json"), "{\"verdict\":\"" + verdict + "\"}");

        return (wroteSnapshot, snapshotPasses, verdict);
    }
}
