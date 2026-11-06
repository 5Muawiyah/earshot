using System.Text;
using System.Text.Json;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Icons;

// src\Earshot\Icon\MingCuteIcons.g.cs is what MingCuteIconGenerator writes from the vendored SVG files, byte for byte. To refresh it
// after changing the converter or the manifest, run this class with EARSHOT_REGENERATE_ICONS=1: it writes the file before comparing,
// and the file is committed like any other source. Line ends are compared as git stores them: a checkout on Windows turns the LF of
// a .cs file into CRLF (.gitattributes), and the generator writes LF.
[TestClass]
public sealed class MingCuteIconGeneratorTests
{
    private static string GeneratedPath() => Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot", "Icon", "MingCuteIcons.g.cs");

    private static string ReadSvg(string name) =>
        File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "third_party", "MingCute", "svg", "core", name.Replace('/', Path.DirectorySeparatorChar) + ".svg"));

    private static JsonDocument Manifest() => VendoredFilesTests.Manifest();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [TestMethod]
    public void TheGeneratedFileMatchesTheVendoredSvgs()
    {
        using JsonDocument manifest = Manifest();
        string expected = MingCuteIconGenerator.Generate(manifest.RootElement, ReadSvg);
        if (Environment.GetEnvironmentVariable("EARSHOT_REGENERATE_ICONS") == "1")
        {
            File.WriteAllText(GeneratedPath(), expected, new UTF8Encoding(false));
        }

        string[] want = expected.Split('\n');
        string[] have = Lf(File.ReadAllText(GeneratedPath())).Split('\n');
        for (int i = 0; i < Math.Max(want.Length, have.Length); i++)
        {
            string a = i < want.Length ? want[i] : "(no line)";
            string b = i < have.Length ? have[i] : "(no line)";
            if (a != b)
            {
                Assert.Fail("MingCuteIcons.g.cs differs from what the generator writes at line " + (i + 1) + ".\ngenerator: " + a + "\ncommitted: " + b);
            }
        }
    }

    // The licence notice the file carries, as the spec words it. Typed out here, not read from the generator.
    [TestMethod]
    public void TheFileCarriesTheDerivedWorkNotice()
    {
        string text = Lf(File.ReadAllText(GeneratedPath()));
        const string Notice =
            "// Derived from MingCute Icons 3.1.0, https://github.com/mingcute-design/mingcute-icons, commit\n" +
            "// 9dc7d3ad074fb16477096a6a8109a6467d0e2fe3, licensed under the Apache License, Version 2.0 (licences\\MingCute\\LICENSE in\n" +
            "// the release, third_party\\MingCute\\LICENSE in the source).\n" +
            "// Changed by Earshot: the SVG path data of each file named below is converted to GDI+ path points (elliptical arcs to cubic\n" +
            "// Bezier curves per the SVG implementation notes, quadratic to cubic); the AirPods icon is also split into its left and right\n" +
            "// buds. No shape is otherwise changed. The source files and their hashes are listed in third_party\\MingCute\\manifest.json.\n" +
            "// Written by tests\\Earshot.Tests\\Icons\\MingCuteIconGenerator.cs; edit the generator, not this file.\n";
        Assert.StartsWith(Notice, text, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-generated", text, StringComparison.OrdinalIgnoreCase, "The file is held to the same analysers as hand-written code, so it carries no generated-code marker.");
    }

    // Constant data only: spans over constants, no field to initialise at start-up and no static constructor.
    [TestMethod]
    public void TheFileHoldsConstantSpansAndNoStaticState()
    {
        string text = File.ReadAllText(GeneratedPath());
        Assert.DoesNotContain("static readonly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("static MingCuteIcons(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new float[", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EveryManifestFileIsInTheOutputUnderItsOwnNameWithTheBudsAfterThePair()
    {
        using JsonDocument manifest = Manifest();
        List<MingCuteIconGenerator.Outline> outlines = MingCuteIconGenerator.Outlines(manifest.RootElement, ReadSvg);
        Assert.HasCount(69 + 4, outlines);
        CollectionAssert.AllItemsAreUnique(outlines.Select(o => o.Name).ToList());
        CollectionAssert.AllItemsAreUnique(outlines.Select(o => o.Property).ToList());
        string[] fromManifest = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()!["assets/svg/core/".Length..^".svg".Length])
            .ToArray();
        CollectionAssert.IsSubsetOf(fromManifest, outlines.Select(o => o.Name).ToList());
        int pair = outlines.FindIndex(o => o.Name == "regular/media/airpods");
        Assert.AreEqual("regular/media/airpods:left", outlines[pair + 1].Name);
        Assert.AreEqual("regular/media/airpods:right", outlines[pair + 2].Name);
        Assert.AreEqual("RegularMediaAirpodsLeft", outlines[pair + 1].Property);
    }

    [TestMethod]
    public void ThePairIsCutIntoItsBudsByTheFiguresOnEachSideOfTheMiddle()
    {
        using JsonDocument manifest = Manifest();
        List<MingCuteIconGenerator.Outline> outlines = MingCuteIconGenerator.Outlines(manifest.RootElement, ReadSvg);
        ConvertedPath Get(string name) => outlines.Single(o => o.Name == name).Path;

        // Line: an outline and its hole in each bud. Filled: one figure each. The buds are the pair's figures and no others.
        Assert.HasCount(2, Get("regular/media/airpods:left").Subpaths);
        Assert.HasCount(2, Get("regular/media/airpods:right").Subpaths);
        Assert.HasCount(1, Get("filled/media/airpods:left").Subpaths);
        Assert.HasCount(1, Get("filled/media/airpods:right").Subpaths);
        foreach (string style in new[] { "regular", "filled" })
        {
            (double _, double _, double leftMax, double _) = SvgPathConverter.Bounds(Get(style + "/media/airpods:left").Subpaths);
            (double rightMin, double _, double _, double _) = SvgPathConverter.Bounds(Get(style + "/media/airpods:right").Subpaths);
            Assert.IsLessThanOrEqualTo(11.0 + 0.01, leftMax, style + ": the left bud stays left of the 2 unit gap");
            Assert.IsGreaterThanOrEqualTo(13.0 - 0.01, rightMin, style + ": the right bud stays right of the 2 unit gap");
            Assert.AreEqual(
                Get(style + "/media/airpods").Subpaths.Count,
                Get(style + "/media/airpods:left").Subpaths.Count + Get(style + "/media/airpods:right").Subpaths.Count,
                style + ": no figure is lost or doubled");
        }
    }

    [TestMethod]
    public void AFigureAcrossTheMiddleRefusesTheCut()
    {
        var across = new ConvertedPath();
        ConvertedSubpath figure = new();
        figure.Points.AddRange([new PathPoint(8, 4), new PathPoint(16, 4), new PathPoint(16, 12)]);
        figure.Types.AddRange([SvgPathConverter.StartType, SvgPathConverter.LineType, (byte)(SvgPathConverter.LineType | SvgPathConverter.CloseFlag)]);
        across.Subpaths.Add(figure);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => MingCuteIconGenerator.SplitBuds(across, "test"));
        StringAssert.Contains(error.Message, "across x = 12");
    }

    [TestMethod]
    public void NumbersAreRoundedToFourDecimalsWithNoNegativeZero()
    {
        Assert.AreEqual(1.2346f, MingCuteIconGenerator.Rounded(1.23456));
        Assert.AreEqual(1.2345f, MingCuteIconGenerator.Rounded(1.23454));
        Assert.AreEqual("0", MingCuteIconGenerator.Rounded(-0.00001).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(12f, MingCuteIconGenerator.Rounded(12.00001));
    }

    [TestMethod]
    public void ThePropertyNameJoinsTheCapitalisedPartsOfTheFileName()
    {
        Assert.AreEqual("RegularSystemSettings3", MingCuteIconGenerator.PropertyName("regular/system/settings_3"));
        Assert.AreEqual("FilledMediaAirpodsRight", MingCuteIconGenerator.PropertyName("filled/media/airpods/right"));
        Assert.AreEqual("RegularArrowArrowLeft", MingCuteIconGenerator.PropertyName("regular/arrow/arrow_left"));
    }
}
