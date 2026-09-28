using Earshot;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// probe widget --out <folder>: RenderProbeWidgets is the function Program.ProbeWidget calls; this drives
// it directly, the same way ProbeTests.cs drives RenderProbeIcons. No IWidgetStatus, no device.
//
// RenderProbeWidgets now builds a WidgetCard, which logs through Paths.Current (FileLog under
// Paths.Current.LogFolder), so every call here runs under EARSHOT_SAFE_MODE and a redirected
// EARSHOT_DATA_ROOT (RenderUnderSafeMode), the same way CompositionRootWidgetTests and PathsTests
// redirect it, so a test run never reaches the real %LOCALAPPDATA%\Earshot even if a DWM call ever
// fails and the card logs a warning.
[TestClass]
public sealed class ProbeWidgetTests
{
    [TestMethod]
    public void EveryFileIsWrittenNonEmpty()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        // Gauge: 2 snapshots x 3 DPIs x 2 inks = 12. Card: 3 variants (this-pc, elsewhere,
        // auto-pause-preview) x 3 DPIs x 2 inks = 18. Case-open card: 3 DPIs x 2 inks = 6. 12+18+6 = 36.
        Assert.HasCount(36, files);
        foreach (Program.ProbeWidgetFile file in files)
        {
            Assert.IsTrue(file.Bytes > 0, file.Snapshot + " " + file.Dpi + " " + file.Ink + ": " + file.Problem);
            Assert.IsNull(file.Problem, file.Snapshot + " " + file.Dpi + " " + file.Ink + ": " + file.Problem);
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

    [TestMethod]
    public void TheCardVariantsAreThisPcElsewhereAndAnAutoPausePreview()
    {
        var variants = Program.ProbeWidgetCardVariants(DateTimeOffset.UtcNow);

        Assert.HasCount(3, variants);
        Assert.AreEqual("this-pc", variants[0].Variant);
        Assert.IsFalse(variants[0].Model.ConnectIntent, "On this PC, connected, so the button reads Disconnect.");
        Assert.IsFalse(variants[0].Model.ShowSwitch, "Production never has AutoPauseAvailable true today.");

        Assert.AreEqual("elsewhere", variants[1].Variant);
        Assert.IsTrue(variants[1].Model.ConnectIntent, "Elsewhere, not connected here, so the button reads Connect.");
        Assert.IsFalse(variants[1].Model.ShowSwitch);

        Assert.AreEqual("auto-pause-preview", variants[2].Variant);
        Assert.IsTrue(variants[2].Model.ShowSwitch, "The preview variant exists to show the switch row.");
        Assert.IsTrue(variants[2].Model.Snapshot.AutoPauseAvailable);
    }

    [TestMethod]
    public void TheCardFilesAreWrittenForEveryVariantDpiAndInk()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        string[] cardFiles = Directory.GetFiles(temp.Path, "card-*.png");
        Assert.HasCount(18, cardFiles, "3 variants x 3 DPIs x 2 inks.");
        foreach (string variant in new[] { "this-pc", "elsewhere", "auto-pause-preview" })
        {
            Assert.HasCount(6, Directory.GetFiles(temp.Path, "card-" + variant + "-*.png"),
                variant + ": 3 DPIs x 2 inks.");
        }

        Program.ProbeWidgetFile cardEntry = files.Single(f =>
            f.Path.Contains("card-this-pc-96dpi", StringComparison.Ordinal) &&
            f.Path.Contains("dark-taskbar-white-ink", StringComparison.Ordinal));
        Assert.IsTrue(cardEntry.Bytes > 0);
        Assert.IsNull(cardEntry.Problem);
    }

    [TestMethod]
    public void TheCaseOpenCardFileIsWrittenForEveryDpiAndInk()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        string[] caseOpenFiles = Directory.GetFiles(temp.Path, "case-open-card-*.png");
        Assert.HasCount(6, caseOpenFiles, "3 DPIs x 2 inks.");
        foreach (string file in caseOpenFiles)
        {
            Assert.IsGreaterThan(0L, new FileInfo(file).Length, file);
        }
    }

    // WriteProbeWidgets (the JSON/text writer) serialises every entry in RenderProbeWidgets' own return
    // list with no filtering (see ProbeWidget.cs), so proving every new variant is in that list is what
    // proves it reaches the JSON and text output too; there is no separate output path to duplicate here.
    [TestMethod]
    public void EveryNewVariantIsInTheListTheJsonAndTextWriterBothSerialise()
    {
        using var temp = new TempFolder();
        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        Assert.IsTrue(files.Any(f => f.Path.Contains("card-this-pc-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("card-elsewhere-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("card-auto-pause-preview-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("case-open-card-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunningTheProbeEndToEndWritesRealNonEmptyFilesAndCleansUpAfterItself()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        Assert.HasCount(36, files);
        Assert.IsTrue(files.All(f => f.Bytes > 0));
        Assert.IsTrue(Directory.Exists(temp.Path));
        // TempFolder's own Dispose (below, via `using`) deletes temp.Path once this test ends, so no
        // scratch output from this test is left on disk.
    }

    // Runs RenderProbeWidgets under EARSHOT_SAFE_MODE=1 and a redirected EARSHOT_DATA_ROOT of its own
    // (deleted again once the call returns), so the card's own FileLog(Paths.Current.LogFolder) never
    // reaches the real %LOCALAPPDATA%\Earshot, whatever this machine's DWM support turns out to be.
    private static IReadOnlyList<Program.ProbeWidgetFile> RenderUnderSafeMode(string outFolder)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "earshot-tests", "probe-widget-data-" + Guid.NewGuid().ToString("N"));
        using var safeMode = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, dataRoot);
        try
        {
            return Program.RenderProbeWidgets(outFolder);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }
}
