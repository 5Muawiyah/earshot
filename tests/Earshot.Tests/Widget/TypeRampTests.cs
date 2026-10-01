using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class TypeRampTests
{
    // The names GDI+ lists on a PC with Segoe UI Variable: long ones are cut at 31 characters.
    private static readonly string[] Installed =
    [
        "Arial", "Segoe UI", "Segoe UI Variable Display", "Segoe UI Variable Display Semib", "Segoe UI Variable Small",
        "Segoe UI Variable Small Semibol", "Segoe UI Variable Text", "Segoe UI Variable Text Semibold", "Segoe UI Variable Text Semiligh",
    ];

    private static readonly int[] Dpis = [96, 120, 144];

    [TestMethod]
    public void EachRoleResolvesToItsOpticalInstanceAmongTheCutNames()
    {
        Assert.AreEqual(new TypeFace("Segoe UI Variable Small", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Caption, Installed, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI Variable Small Semibol", FontStyle.Regular), TypeRamp.Resolve(TypeRole.CaptionStrong, Installed, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI Variable Text", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Body, Installed, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI Variable Text Semibold", FontStyle.Regular), TypeRamp.Resolve(TypeRole.BodyStrong, Installed, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI Variable Display Semib", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Number, Installed, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI Variable Small", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Gauge, Installed, "Message"));
    }

    [TestMethod]
    public void SemiboldNeverUsesBoldOnAVariableFamily()
    {
        foreach (TypeRole role in Enum.GetValues<TypeRole>())
        {
            TypeFace face = TypeRamp.Resolve(role, Installed, "Message");
            Assert.AreEqual(FontStyle.Regular, face.Style, role + " is a named instance, not a bold of another.");
        }
    }

    [TestMethod]
    public void WithoutVariableFontsTheFallbackIsSegoeUiThenTheMessageFont()
    {
        string[] noVariable = ["Arial", "Segoe UI"];
        Assert.AreEqual(new TypeFace("Segoe UI", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Body, noVariable, "Message"));
        Assert.AreEqual(new TypeFace("Segoe UI", FontStyle.Bold), TypeRamp.Resolve(TypeRole.BodyStrong, noVariable, "Message"), "Segoe UI has one bold weight and no semibold.");
        string[] nothing = ["Arial"];
        Assert.AreEqual(new TypeFace("Message", FontStyle.Regular), TypeRamp.Resolve(TypeRole.Caption, nothing, "Message"));
        Assert.AreEqual(new TypeFace("Message", FontStyle.Bold), TypeRamp.Resolve(TypeRole.CaptionStrong, nothing, "Message"));
    }

    [TestMethod]
    public void SizesAndLineHeightsFollowTheRampTheDpiAndTheTextSize()
    {
        Assert.AreEqual(12, TypeRamp.SizePx(TypeRole.Caption, 96, 1.0));
        Assert.AreEqual(14, TypeRamp.SizePx(TypeRole.Body, 96, 1.0));
        Assert.AreEqual(16, TypeRamp.LineHeight(TypeRole.Caption, 96, 1.0));
        Assert.AreEqual(20, TypeRamp.LineHeight(TypeRole.BodyStrong, 96, 1.0));
        Assert.AreEqual(24, TypeRamp.LineHeight(TypeRole.Number, 96, 1.0));
        Assert.AreEqual(15, TypeRamp.SizePx(TypeRole.Caption, 120, 1.0));
        Assert.AreEqual(21, TypeRamp.SizePx(TypeRole.Body, 144, 1.0));
        Assert.AreEqual(18, TypeRamp.SizePx(TypeRole.Caption, 96, 1.5));
        Assert.AreEqual(27, TypeRamp.SizePx(TypeRole.Caption, 96, 2.25));
        Assert.AreEqual(45, TypeRamp.SizePx(TypeRole.Number, 96, 2.25));
        Assert.AreEqual(54, TypeRamp.LineHeight(TypeRole.Caption, 144, 2.25), "16 x 1.5 x 2.25.");
    }

    [TestMethod]
    public void TheTextSizeIsClampedToWhatWindowsOffers()
    {
        Assert.AreEqual(12, TypeRamp.SizePx(TypeRole.Caption, 96, 0.5), "Below 100% is read as 100%.");
        Assert.AreEqual(27, TypeRamp.SizePx(TypeRole.Caption, 96, 9.0), "Above 225% is read as 225%.");
        Assert.AreEqual(12, TypeRamp.SizePx(TypeRole.Caption, 96, double.NaN));
    }

    [TestMethod]
    public void TheGaugeRoleIgnoresTheTextSize()
    {
        foreach (int dpi in Dpis)
        {
            Assert.AreEqual(TypeRamp.SizePx(TypeRole.Gauge, dpi, 1.0), TypeRamp.SizePx(TypeRole.Gauge, dpi, 2.25));
            Assert.IsGreaterThan(TypeRamp.SizePx(TypeRole.Caption, dpi, 1.0), TypeRamp.SizePx(TypeRole.Caption, dpi, 2.25));
        }
    }

    [TestMethod]
    public void ARealFontComesBackInPixelsAtTheRampsSize()
    {
        foreach (TypeRole role in Enum.GetValues<TypeRole>())
        {
            using Font font = TypeRamp.Font(role, 120, 1.5);
            Assert.AreEqual(GraphicsUnit.Pixel, font.Unit);
            Assert.AreEqual(TypeRamp.SizePx(role, 120, 1.5), (int)font.Size, role.ToString());
        }
    }
}
