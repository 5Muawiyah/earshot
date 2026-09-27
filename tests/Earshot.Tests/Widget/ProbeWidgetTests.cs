using Earshot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// probe widget --out <folder>: RenderProbeWidgets is the function Program.ProbeWidget calls; this drives
// it directly, the same way ProbeTests.cs drives RenderProbeIcons. No IWidgetStatus, no device.
[TestClass]
public sealed class ProbeWidgetTests
{
    [TestMethod]
    public void EveryFileIsWrittenNonEmpty()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = Program.RenderProbeWidgets(temp.Path);

        // 2 snapshots x 3 DPIs x 2 inks.
        Assert.HasCount(12, files);
        foreach (Program.ProbeWidgetFile file in files)
        {
            Assert.IsTrue(file.Bytes > 0, file.Snapshot + " " + file.Dpi + " " + file.Ink + ": " + file.Problem);
            Assert.IsTrue(File.Exists(file.Path));
        }

        Assert.HasCount(12, Directory.GetFiles(temp.Path, "gauge-*.png"));
    }

    [TestMethod]
    public void TheTwoSnapshotsAreThisPcAndElsewhere()
    {
        var snapshots = Earshot.Program.ProbeWidgetSnapshots(DateTimeOffset.UtcNow);
        Assert.HasCount(2, snapshots);
        Assert.AreEqual(Earshot.Widget.AirPodsWhere.ThisPc, snapshots[0].Snapshot.Where);
        Assert.AreEqual(Earshot.Widget.AirPodsWhere.Elsewhere, snapshots[1].Snapshot.Where);
    }
}
