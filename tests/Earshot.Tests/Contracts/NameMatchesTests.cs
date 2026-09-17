using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// NodeMatch.NameMatches is an ordinal, case-insensitive substring test. It does not fold
// apostrophes: Windows names the AirPods with a curly U+2019, and a straight apostrophe typed by
// the user does not match it. These tests pin that behaviour.
[TestClass]
public sealed class NameMatchesTests
{
    private const char CurlyApostrophe = (char)0x2019;

    private static readonly string WindowsName = "Jonathan" + CurlyApostrophe + "s AirPods Pro";

    [TestMethod]
    public void DefaultMatchFindsTheCurlyApostropheName()
    {
        Assert.IsTrue(NodeMatch.NameMatches(WindowsName, new EarshotSettings().DeviceMatch));
    }

    [TestMethod]
    [DataRow("airpods")]
    [DataRow("AIRPODS")]
    [DataRow("AirPods Pro")]
    [DataRow("aIrPoDs pRo")]
    public void IgnoresCase(string match)
    {
        Assert.IsTrue(NodeMatch.NameMatches(WindowsName, match));
    }

    [TestMethod]
    public void CurlyApostropheMatchesCurly()
    {
        Assert.IsTrue(NodeMatch.NameMatches(WindowsName, "Jonathan" + CurlyApostrophe + "s"));
        Assert.IsTrue(NodeMatch.NameMatches(WindowsName, "JONATHAN" + CurlyApostrophe + "S AIRPODS"));
    }

    [TestMethod]
    public void StraightApostropheDoesNotMatchCurly()
    {
        Assert.IsFalse(NodeMatch.NameMatches(WindowsName, "Jonathan's"));
        Assert.IsFalse(NodeMatch.NameMatches("Jonathan's AirPods Pro", "Jonathan" + CurlyApostrophe + "s"));
    }

    [TestMethod]
    [DataRow("[AirPods]")]
    [DataRow("Air*")]
    [DataRow("Air?ods")]
    [DataRow("`AirPods")]
    public void WildcardCharactersAreLiteral(string match)
    {
        Assert.IsFalse(NodeMatch.NameMatches(WindowsName, match));
        Assert.IsTrue(NodeMatch.NameMatches("x" + match + "y", match));
    }

    [TestMethod]
    public void NullNameNeverMatches()
    {
        Assert.IsFalse(NodeMatch.NameMatches(null, "AirPods"));
    }

    [TestMethod]
    public void ADifferentDeviceDoesNotMatch()
    {
        Assert.IsFalse(NodeMatch.NameMatches("iPhone", "AirPods"));
        Assert.IsFalse(NodeMatch.NameMatches("iPhone Hands-Free HF Audio", "AirPods"));
    }
}
