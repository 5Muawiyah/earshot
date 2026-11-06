using System.Drawing;
using Earshot;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// probe widget --out <folder> --set icons: RenderProbeWidgetIconsSet is what Program.ProbeWidget calls for the option, so this
// drives it directly under safe mode with a data root of its own, as the design set's tests do.
[TestClass]
public sealed class ProbeWidgetIconsSetTests
{
    private static readonly string[] Pictures = ["icons-catalogue", "icons-tray", "icons-gauge", "icons-card", "icons-menu"];

    private static readonly string[] Themes = ["light", "dark", "high-contrast"];

    private static readonly string[] Scales = ["100pct", "125pct", "150pct", "200pct"];

    [TestMethod]
    public void TheSetWritesEveryPictureInEveryThemeAtEveryScale()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeWidgetFile> files = RenderUnderSafeMode(temp.Path);

        Assert.HasCount(Pictures.Length * Themes.Length * Scales.Length, files);
        foreach (Program.ProbeWidgetFile file in files)
        {
            Assert.IsNull(file.Problem, file.Path + ": " + file.Problem);
            Assert.IsTrue(file.Bytes > 0, file.Path);
            Assert.IsTrue(new FileInfo(file.Path).Length > 0, file.Path);
        }

        foreach (string picture in Pictures)
        {
            foreach (string theme in Themes)
            {
                foreach (string scale in Scales)
                {
                    string path = Path.Combine(temp.Path, picture + "-" + theme + "-" + scale + ".png");
                    Assert.IsTrue(File.Exists(path), path);
                    using var bitmap = new Bitmap(path);
                    Assert.IsTrue(bitmap.Width > 0 && bitmap.Height > 0, path);
                }
            }
        }
    }

    [TestMethod]
    public void TheCatalogueSheetGrowsWithTheScaleAndTheThemesDiffer()
    {
        using var temp = new TempFolder();

        RenderUnderSafeMode(temp.Path);

        using var small = new Bitmap(Path.Combine(temp.Path, "icons-catalogue-light-100pct.png"));
        using var large = new Bitmap(Path.Combine(temp.Path, "icons-catalogue-light-200pct.png"));
        Assert.IsGreaterThan(small.Width, large.Width);
        Assert.IsGreaterThan(small.Height, large.Height);
        byte[] light = File.ReadAllBytes(Path.Combine(temp.Path, "icons-menu-light-100pct.png"));
        byte[] dark = File.ReadAllBytes(Path.Combine(temp.Path, "icons-menu-dark-100pct.png"));
        Assert.IsFalse(light.AsSpan().SequenceEqual(dark));
    }

    [TestMethod]
    public void TheSetOptionAcceptsIconsForTheWidgetProbe()
    {
        Assert.IsTrue(Program.TryParseProbeArgs(["probe", "widget", "--out", @"C:\x", "--set", "icons"], out Program.ProbeRequest? request, out string? error), error);
        Assert.AreEqual("icons", request!.Set);
        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "battery", "--set", "icons"], out _, out _), "Only probe widget takes a set.");
    }

    private static IReadOnlyList<Program.ProbeWidgetFile> RenderUnderSafeMode(string outFolder)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "earshot-tests", "probe-widget-icons-data-" + Guid.NewGuid().ToString("N"));
        using var safeMode = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, dataRoot);
        try
        {
            return Program.RenderProbeWidgetIconsSet(outFolder);
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
