using System.Drawing;
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

        // Gauge: 3 snapshots x 3 DPIs x 2 inks = 18. Card: every variant x 3 DPIs x 2 inks. Case-open card:
        // 3 DPIs x 2 inks = 6.
        Assert.HasCount(18 + (AllCardVariants.Length * 6) + 6, files, "18 gauge files, every card variant, and the case-open card.");
        foreach (Program.ProbeWidgetFile file in files)
        {
            Assert.IsTrue(file.Bytes > 0, file.Snapshot + " " + file.Dpi + " " + file.Ink + ": " + file.Problem);
            Assert.IsNull(file.Problem, file.Snapshot + " " + file.Dpi + " " + file.Ink + ": " + file.Problem);
            Assert.IsTrue(File.Exists(file.Path));
        }

        Assert.HasCount(18, Directory.GetFiles(temp.Path, "gauge-*.png"));
    }

    [TestMethod]
    public void TheSnapshotsAreThisPcElsewhereAndTheSameReadingGreyed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshots = Earshot.Program.ProbeWidgetSnapshots(now);
        Assert.HasCount(3, snapshots);
        Assert.AreEqual(Earshot.Widget.AirPodsWhere.ThisPc, snapshots[0].Snapshot.Where);
        Assert.AreEqual(Earshot.Widget.AirPodsWhere.Elsewhere, snapshots[1].Snapshot.Where);
        Assert.AreEqual(Earshot.Widget.AirPodsWhere.ThisPc, snapshots[2].Snapshot.Where);
        Assert.IsTrue(BatteryFreshness.IsFresh(snapshots[0].Snapshot.Left, now), "this-pc shows fresh values.");
        Assert.IsFalse(BatteryFreshness.IsFresh(snapshots[2].Snapshot.Left, now), "greyed shows values that are not fresh.");
        Assert.AreEqual(ReadingKind.Last, BatteryFreshness.Shown(snapshots[2].Snapshot, now).Gauge?.Kind, "...and the gauge draws them as a last reading.");
    }

    private static readonly string[] AllCardVariants = ["this-pc", "elsewhere", "greyed", "no-reading", "open-the-case", "auto-pause-preview", "refresh-reading", "refresh-nothing-heard"];

    // The files of one variant only, matched up to the DPI that always follows the name.
    private static string[] VariantFiles(string folder, string variant) =>
        Directory.GetFiles(folder, "card-*.png")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^card-" + System.Text.RegularExpressions.Regex.Escape(variant) + @"-\d+dpi-"))
            .ToArray();

    [TestMethod]
    public void TheCardVariantsAreFreshGreyedAndEmpty()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var variants = Program.ProbeWidgetCardVariants(now);

        CollectionAssert.AreEqual(AllCardVariants, variants.Select(v => v.Variant).ToArray());
        Assert.IsFalse(variants[0].Model.ConnectIntent, "On this PC, connected, so the button reads Disconnect.");
        Assert.IsFalse(variants[0].Model.ShowSwitch, "Production never has AutoPauseAvailable true today.");
        Assert.IsTrue(variants[1].Model.ConnectIntent, "Elsewhere, not connected here, so the button reads Connect.");
        Assert.IsTrue(variants[0].Model.ShownParts.Left.Fresh, "The first card shows fresh values.");
        Assert.IsFalse(variants[2].Model.ShownParts.Left.Fresh, "The greyed card shows values that are not fresh.");
        Assert.AreEqual(70, variants[2].Model.ShownParts.Left.Percent, "...and still shows them.");
        Assert.IsNull(variants[3].Model.Snapshot.Left.Percent, "The empty card has no value for any part.");
        Assert.AreEqual("open-the-case", variants[4].Variant);
        Assert.AreEqual(AirPodsWhere.ThisPc, variants[4].Model.Snapshot.Where);
        Assert.AreEqual(BroadcastSelectionState.Listening, variants[4].Model.Snapshot.Selection, "Connected with no pair linked.");
        Assert.IsTrue(variants[5].Model.ShowSwitch, "The preview variant exists to show the switch row.");
        Assert.IsTrue(variants[5].Model.Snapshot.AutoPauseAvailable);
        Assert.IsTrue(variants[6].Model.Refresh!.Reading, "The refresh-reading variant shows the icon mid-turn.");
        Assert.AreEqual("Reading the battery", variants[6].Model.Refresh!.ReadLine);
        Assert.AreEqual(BatteryRefreshOutcome.NothingHeard, variants[7].Model.Refresh!.Outcome);
        Assert.AreEqual("Nothing heard. Open the case", variants[7].Model.Refresh!.ReadLine);
    }

    [TestMethod]
    public void TheCardFilesAreWrittenForEveryVariantDpiAndInk()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        string[] cardFiles = Directory.GetFiles(temp.Path, "card-*.png");
        Assert.HasCount(AllCardVariants.Length * 6, cardFiles, AllCardVariants.Length + " variants x 3 DPIs x 2 inks.");
        foreach (string variant in AllCardVariants)
        {
            Assert.HasCount(6, VariantFiles(temp.Path, variant), variant + ": 3 DPIs x 2 inks.");
        }

        Program.ProbeWidgetFile cardEntry = files.Single(f =>
            f.Path.Contains("card-this-pc-96dpi", StringComparison.Ordinal) &&
            f.Path.Contains("dark-taskbar-white-ink", StringComparison.Ordinal));
        Assert.IsTrue(cardEntry.Bytes > 0);
        Assert.IsNull(cardEntry.Problem);
    }

    // Every variant is drawn for both themes at every DPI with the file names the rest of the probe uses.
    [TestMethod]
    public void EveryVariantFileExistsInLightAndDark()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        foreach (string variant in AllCardVariants)
        {
            string[] at96 = VariantFiles(temp.Path, variant).Where(f => Path.GetFileName(f).Contains("-96dpi-", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, at96.Where(f => f.EndsWith("-dark-taskbar-white-ink.png", StringComparison.Ordinal)).ToArray(), variant + " in dark");
            Assert.HasCount(1, at96.Where(f => f.EndsWith("-light-taskbar-black-ink.png", StringComparison.Ordinal)).ToArray(), variant + " in light");
        }
    }

    // The Form behind a card capture is never shown, so DWM never actually composites its translucent
    // backdrop onto it, and Control.DrawToBitmap cannot recover or preserve real per-pixel alpha from
    // OnPaint's own transparent clear (GDI's own text and fill calls force it to opaque as they go): a
    // corner pixel with no fix reads straight back as opaque black, not the intended backdrop. Reading the
    // corner pixel of an actual saved file is the only proof the override reaches the saved bitmap.
    [TestMethod]
    public void ACardCaptureIsCompositedOntoTheMeasuredBackdropForItsInk()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        Program.ProbeWidgetFile darkInkEntry = files.Single(f =>
            f.Path.Contains("card-this-pc-96dpi", StringComparison.Ordinal) &&
            f.Path.Contains("dark-taskbar-white-ink", StringComparison.Ordinal));
        Program.ProbeWidgetFile lightInkEntry = files.Single(f =>
            f.Path.Contains("card-this-pc-96dpi", StringComparison.Ordinal) &&
            f.Path.Contains("light-taskbar-black-ink", StringComparison.Ordinal));

        Assert.AreEqual(Program.DarkThemeBackdrop.ToArgb(), CornerPixel(darkInkEntry.Path),
            "White ink means the taskbar is dark, so the capture should sit on the dark backdrop.");
        Assert.AreEqual(Program.LightThemeBackdrop.ToArgb(), CornerPixel(lightInkEntry.Path),
            "Black ink means the taskbar is light, so the capture should sit on the light backdrop.");
    }

    private static int CornerPixel(string path)
    {
        using var bitmap = new Bitmap(path);
        return bitmap.GetPixel(0, 0).ToArgb();
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
        Assert.IsTrue(files.Any(f => f.Path.Contains("card-greyed-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("card-no-reading-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("card-auto-pause-preview-", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(f => f.Path.Contains("case-open-card-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunningTheProbeEndToEndWritesRealNonEmptyFilesAndCleansUpAfterItself()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        Assert.HasCount(18 + (AllCardVariants.Length * 6) + 6, files, "18 gauge files, every card variant, and the case-open card.");
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
