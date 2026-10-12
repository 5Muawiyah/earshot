using System.Drawing;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The tester draws each of its views to PNG from made-up data, in both themes at both scales, so a designer can see the window
// without running a test. Drawn on a private desktop, from nothing real.
[TestClass]
public sealed class TesterPicturesTests
{
    private static readonly string[] ViewNames = ["home", "list", "runall", "step", "waiting", "pass", "fail", "handover"];

    private static (int Differing, int Sampled, double Luminance) Measure(string path)
    {
        using var bitmap = new Bitmap(path);
        Color corner = bitmap.GetPixel(bitmap.Width - 2, bitmap.Height - 2);
        int differing = 0;
        int sampled = 0;
        double luminance = 0;
        for (int y = 0; y < bitmap.Height; y += 4)
        {
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                Color pixel = bitmap.GetPixel(x, y);
                sampled++;
                luminance += (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
                if (pixel.ToArgb() != corner.ToArgb())
                {
                    differing++;
                }
            }
        }

        return (differing, sampled, luminance / sampled);
    }

    [TestMethod]
    public void EveryViewIsDrawnInBothThemesAtBothScalesAndNoneIsBlank()
    {
        using var folder = new TempFolder();
        var written = new List<string>();
        Phase5.CardDesktop.Run(() => written.AddRange(TesterPictures.RenderAll(folder.Path)), TimeSpan.FromMinutes(4));

        Assert.HasCount(ViewNames.Length * 2 * 2, written);
        var luminance = new Dictionary<string, double>();
        foreach (string view in ViewNames)
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                foreach (int percent in new[] { 100, 150 })
                {
                    string name = view + "-" + theme + "-" + percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pct.png";
                    string path = Path.Combine(folder.Path, name);
                    Assert.IsTrue(File.Exists(path), name + " was not written.");
                    (int differing, int sampled, double mean) = Measure(path);
                    Assert.IsGreaterThan(sampled / 25, differing, name + " is blank: " + differing + " of " + sampled + " sampled pixels differ from the corner.");
                    luminance[name] = mean;
                }
            }
        }

        foreach (string view in ViewNames)
        {
            foreach (int percent in new[] { 100, 150 })
            {
                string at = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsLessThan(luminance[view + "-light-" + at + "pct.png"] - 40, luminance[view + "-dark-" + at + "pct.png"], view + ": the dark picture is not darker than the light one.");
            }
        }

        using var small = new Bitmap(Path.Combine(folder.Path, "home-light-100pct.png"));
        using var large = new Bitmap(Path.Combine(folder.Path, "home-light-150pct.png"));
        Assert.IsGreaterThan(small.Width, large.Width, "150% is not larger than 100%.");
    }

    [TestMethod]
    public void NamesFollowViewThemeAndScale()
    {
        Assert.AreEqual("step-dark-150pct.png", TesterPictures.FileName("step", "dark", 150));
    }

    [TestMethod]
    public void NothingRealIsInTheDrawnData()
    {
        string root = RepositoryLocator.RepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "Earshot.TestWindow", "Ui", "TesterPictures.cs"));
        StringAssert.DoesNotMatch(source, new System.Text.RegularExpressions.Regex(@"[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}"), "A Bluetooth address is in the picture data.");
        StringAssert.DoesNotMatch(source, new System.Text.RegularExpressions.Regex(@"\\Users\\(?!Example)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "A user folder is in the picture data.");
    }
}
