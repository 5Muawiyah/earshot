using System.Drawing;
using Earshot;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// probe widget --out <folder> --set design: RenderProbeWidgetDesignSet is what Program.ProbeWidget calls for
// the option, so this drives it directly. Every render runs under safe mode with a data root of its own, as the
// default set's tests do.
[TestClass]
public sealed class ProbeWidgetDesignSetTests
{
    private static readonly string[] CardPictures =
    [
        "card-fresh", "card-greyed", "card-refreshing", "card-refresh-open-the-case",
        "card-fresh-text-150", "card-greyed-text-150", "card-estimates", "card-estimates-text-150", "card-bluetooth-off", "card-case-open",
        "card-case-open-estimates", "settings", "settings-text-150", "settings-more-open", "settings-order-open", "settings-case-open-card-open",
        "history", "update-up-to-date", "update-available", "update-downloading",
    ];

    private static readonly string[] Themes = ["light", "dark", "high-contrast"];

    private static readonly string[] GaugeStates =
    [
        "gauge-reading", "gauge-charging", "gauge-low", "gauge-no-recent-reading", "gauge-not-on-this-pc",
        "gauge-on-the-other-device", "gauge-last-reading", "gauge-estimate", "gauge-away-case-last-reading", "gauge-away-case-estimate",
        "gauge-away-case-charging", "gauge-away-case-live",
    ];

    private static readonly string[] GaugeOrders =
    [
        "gauge-order-ring-number-bolt", "gauge-order-ring-bolt-number", "gauge-order-number-ring-bolt",
        "gauge-order-number-bolt-ring", "gauge-order-bolt-ring-number", "gauge-order-bolt-number-ring",
    ];

    [TestMethod]
    public void TheSetRendersEveryPictureNonEmptyInEveryThemeAtBothScales()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        int pictures = CardPictures.Length + GaugeStates.Length + GaugeOrders.Length;
        Assert.HasCount(pictures * 3 * 2, files, "Every picture in light, dark and high contrast at 100% and 150%.");
        foreach (Program.ProbeWidgetFile file in files)
        {
            Assert.IsNull(file.Problem, file.Path + ": " + file.Problem);
            Assert.IsTrue(file.Bytes > 0, file.Path);
            Assert.IsTrue(new FileInfo(file.Path).Length > 0, file.Path);
        }

        foreach (string name in CardPictures.Concat(GaugeStates).Concat(GaugeOrders))
        {
            foreach (string theme in Themes)
            {
                foreach (string scale in new[] { "100pct", "150pct" })
                {
                    Assert.IsTrue(File.Exists(Path.Combine(temp.Path, name + "-" + theme + "-" + scale + ".png")), name + " " + theme + " " + scale);
                }
            }
        }
    }

    [TestMethod]
    public void EveryGaugeFileIsTheFixedSizeForItsScale()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        foreach (string name in GaugeStates.Concat(GaugeOrders))
        {
            foreach (string theme in Themes)
            {
                AssertSize(Path.Combine(temp.Path, name + "-" + theme + "-100pct.png"), 74, 40);
                AssertSize(Path.Combine(temp.Path, name + "-" + theme + "-150pct.png"), 111, 60);
            }
        }
    }

    // The settings page draws the Gauge order picker and the Microphone off row from its values; the update page
    // draws its own sub-page. Both are taller than the card, so the file shows more than the main view's size does.
    [TestMethod]
    public void ThePagesAreNotTheMainCardsSize()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        Size main = SizeOf(Path.Combine(temp.Path, "card-fresh-light-100pct.png"));
        Size settings = SizeOf(Path.Combine(temp.Path, "settings-light-100pct.png"));
        Size update = SizeOf(Path.Combine(temp.Path, "update-available-light-100pct.png"));
        Assert.IsGreaterThan(main.Height, settings.Height, "The settings page lists rows the card does not.");
        Assert.AreNotEqual(main, update);
        Size large = SizeOf(Path.Combine(temp.Path, "card-fresh-text-150-light-100pct.png"));
        Assert.IsGreaterThan(main.Height, large.Height, "Larger Windows text makes the card taller.");
    }

    [TestMethod]
    public void TheSixOrdersAreSixDifferentPictures()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in GaugeOrders)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(temp.Path, name + "-light-100pct.png"));
            Assert.IsTrue(seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), name + " matches another order.");
        }
    }

    [TestMethod]
    public void TheSetOptionIsParsedForTheWidgetProbeOnly()
    {
        Assert.IsTrue(Program.TryParseProbeArgs(["probe", "widget", "--out", @"C:\x", "--set", "design"], out Program.ProbeRequest? request, out string? error), error);
        Assert.AreEqual("design", request!.Set);

        Assert.IsTrue(Program.TryParseProbeArgs(["probe", "widget", "--out", @"C:\x"], out Program.ProbeRequest? plain, out _));
        Assert.IsNull(plain!.Set);

        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "widget", "--out", @"C:\x", "--set", "other"], out _, out _), "An unknown set is refused.");
        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "widget", "--out", @"C:\x", "--set"], out _, out _), "--set needs a name.");
        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "battery", "--set", "design"], out _, out _), "Only probe widget takes a set.");
    }

    // Every new state is in the set: the case-open card, estimates, last readings on the gauge, the case mark, the history page and the
    // More row open.
    [TestMethod]
    public void TheSetCoversTheCaseOpenCardEstimatesLastReadingsTheCaseMarkHistoryAndMoreOpen()
    {
        string[] required =
        [
            "card-case-open", "card-estimates", "gauge-last-reading", "gauge-estimate", "gauge-away-case-last-reading", "gauge-away-case-estimate",
            "gauge-away-case-charging", "gauge-away-case-live", "history", "settings-more-open", "settings-order-open",
        ];
        foreach (string name in required)
        {
            Assert.IsTrue(CardPictures.Concat(GaugeStates).Contains(name), name + " is in the set.");
        }

        Assert.HasCount(6, Program.ProbeWidgetDesignGaugeContents());
    }

    [TestMethod]
    public void NothingInTheSnapshotsNamesADeviceOrAnAddress()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.HasCount(6, Program.ProbeWidgetDesignGaugeStates(now));
        foreach ((string name, WidgetSnapshot snapshot) in Program.ProbeWidgetDesignGaugeStates(now))
        {
            Assert.IsFalse(name.Any(char.IsUpper), name);
            Assert.IsNotNull(snapshot);
        }
    }

    private static void AssertSize(string path, int width, int height)
    {
        Size size = SizeOf(path);
        Assert.AreEqual(new Size(width, height), size, Path.GetFileName(path));
    }

    private static Size SizeOf(string path)
    {
        using var bitmap = new Bitmap(path);
        return bitmap.Size;
    }

    private static IReadOnlyList<Program.ProbeWidgetFile> RenderUnderSafeMode(string outFolder)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "earshot-tests", "probe-widget-design-data-" + Guid.NewGuid().ToString("N"));
        using var safeMode = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, dataRoot);
        try
        {
            return Program.RenderProbeWidgetDesignSet(outFolder);
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
