using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// plain-window-layout.md's own acceptance check for Home, the first of the two this commit is
// owed: "If a run is waiting after a shut down: the primary button becomes 'Carry on with the
// tests' and a line says which test it will pick up." UpdateRunAllButtonLabel already reads
// run-all.json fresh at every open (RunAllPresentationTests.cs proves the underlying label logic
// through a real Run all click); this drives the same read the other way round, through a fresh
// window's own real Activated handler, and checks it through the actual Home view text, not a
// presenter fact in isolation.
[TestClass]
public sealed class HomeCarryOnPresentationTests
{
    [TestMethod]
    public void HomeShowsCarryOnAndNamesTheTestItWillPickUpAtOnceARunIsWaiting()
    {
        using var sandbox = new TempFolder();
        string windowStateRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest-gui");

        // Row "08" is RunAllOrder.Items' own ninth entry; its pointed-at DisplayRow's plain Name
        // is what the pickup line must name.
        int index08 = RunAllOrder.Items.ToList().FindIndex(item => item.Key == "08");
        Assert.IsTrue(index08 >= 0, "\"08\" was not found in RunAllOrder.Items.");
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(RunAllFile.Key).ToArray(),
            StoppedAtIndex = index08,
        });

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();

            // A fresh window (nothing has run through it yet) already reads run-all.json at
            // construction (UpdateRunAllButtonLabel/UpdateHomeRunAllInfo, both called from
            // PopulateRows), so the label is already right without needing Activated; Activated is
            // exercised too, the same "owner alt-tabbing back" path StartGateReadsFreshBannerTests.cs
            // already proves for the banner.
            Assert.AreEqual(Copy.RunAllCarryOnLabel, form.RunAllButtonTextForTests,
                "Home's primary button did not become 'Carry on with the tests' for a waiting run.");
            Assert.IsTrue(form.HomePickupLineVisibleForTests, "the pickup line was not shown.");
            Assert.AreEqual(Copy.HomeCarryOnPickupLine("Full shut down and start"), form.HomePickupLineTextForTests);

            form.RaiseActivatedForTests();

            Assert.AreEqual(Copy.RunAllCarryOnLabel, form.RunAllButtonTextForTests);
            Assert.IsTrue(form.HomePickupLineVisibleForTests);
            Assert.AreEqual(Copy.HomeCarryOnPickupLine("Full shut down and start"), form.HomePickupLineTextForTests);
        });
    }

    [TestMethod]
    public void HomeShowsTheFreshStartLabelAndNoPickupLineWhenNothingIsWaiting()
    {
        using var sandbox = new TempFolder();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();

            Assert.AreEqual(Copy.RunAllButtonLabel, form.RunAllButtonTextForTests);
            Assert.IsFalse(form.HomePickupLineVisibleForTests, "the pickup line must not show when no run is waiting.");
        });
    }

    // The "how many tests" line under the primary button must be built from the real manifest
    // (RunAllOrder.Items), never a typed number that could drift from what Run all actually runs.
    [TestMethod]
    public void HomesCountLineNamesTheRealNumberOfRunAllItemsAndFullShutDownTests()
    {
        using var sandbox = new TempFolder();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();

            StringAssert.Contains(form.HomeCountLineTextForTests, RunAllOrder.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
    }
}
