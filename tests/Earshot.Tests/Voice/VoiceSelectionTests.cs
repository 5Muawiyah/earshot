using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// VoiceSelection.Choose is the rule SystemSpeechEngine.Open uses before it ever calls the real
// SelectVoice, so it is tested here as a pure function over a list of (name, enabled), with no
// SpeechSynthesizer involved at all (see VoiceSelection's own header for why this exists).
[TestClass]
public sealed class VoiceSelectionTests
{
    private static readonly (string Name, bool Enabled)[] Installed =
    [
        ("Microsoft Zira Desktop", true),
        ("Microsoft David Desktop", true),
        ("Microsoft Old Voice", false),
    ];

    [TestMethod]
    public void NoMatchReturnsNull()
    {
        Assert.IsNull(VoiceSelection.Choose("Nobody Here", Installed));
    }

    [TestMethod]
    public void ExactMatchReturnsTheInstalledName()
    {
        Assert.AreEqual("Microsoft Zira Desktop", VoiceSelection.Choose("Microsoft Zira Desktop", Installed));
    }

    [TestMethod]
    public void SubstringMatchReturnsTheInstalledName()
    {
        // SelectVoice(String) itself matches by substring, so "Zira" alone must resolve the same way.
        Assert.AreEqual("Microsoft Zira Desktop", VoiceSelection.Choose("Zira", Installed));
    }

    [TestMethod]
    public void ADisabledVoiceIsNeverChosenEvenWhenItMatches()
    {
        Assert.IsNull(VoiceSelection.Choose("Old Voice", Installed));
    }

    [TestMethod]
    public void EmptyRequestedReturnsNullWithoutSearching()
    {
        Assert.IsNull(VoiceSelection.Choose(string.Empty, Installed));
    }

    [TestMethod]
    public void NullRequestedReturnsNull()
    {
        Assert.IsNull(VoiceSelection.Choose(null, Installed));
    }

    [TestMethod]
    public void AVeryLongRequestedNameThatMatchesNothingReturnsNullRatherThanThrowing()
    {
        string veryLong = new('x', 10_000);
        Assert.IsNull(VoiceSelection.Choose(veryLong, Installed));
    }

    [TestMethod]
    public void MarkupInTheRequestedNameIsTreatedAsOrdinaryTextNotSearchedAsAPattern()
    {
        Assert.IsNull(VoiceSelection.Choose("<voice>Zira</voice>", Installed));
    }

    [TestMethod]
    public void EmbeddedCarriageReturnsAndNewlinesDoNotMatchAndDoNotThrow()
    {
        Assert.IsNull(VoiceSelection.Choose("Zira\r\nDesktop", Installed));
    }

    [TestMethod]
    public void AnEmptyInstalledListReturnsNull()
    {
        Assert.IsNull(VoiceSelection.Choose("Zira", []));
    }
}
