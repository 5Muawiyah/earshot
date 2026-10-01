using System.Text.RegularExpressions;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The widget shows no battery figure, current or greyed, unless the AirPods are connected to this PC (BatteryFreshness). A live
// test that shuts the AirPods in the case disconnects them, so a prompt that expects the figures to be "still greyed" there
// contradicts what the build does, and an owner who answered "no" to it would have been reporting a fault that is not one. Held
// here, by reading the prompts the way the owner reads them: each prompt about a figure that is old says that it may be gone, the
// prompt that needs figures first connects the AirPods, and a comparison with the iPhone says what to answer when nothing is shown.
[TestClass]
public sealed class WidgetLiveTestConnectedOnlyTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "19-Widget.ps1"));

    private static string Quoted(string script, string startsWith)
    {
        Match m = Regex.Match(script, "'(" + Regex.Escape(startsWith) + "(?:[^']|'')*)'");
        Assert.IsTrue(m.Success, "No prompt in test 19 starts with: " + startsWith);
        return m.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EveryPromptAboutAnOldFigureAllowsItToBeGoneBecauseTheAirPodsAreNotConnected()
    {
        string script = Script();

        string greyed = Quoted(script, "A minute later");
        StringAssert.Contains(greyed, "gone");
        StringAssert.Contains(greyed, "not on this PC");
        StringAssert.Contains(greyed, "still connected");

        string nothingHeard = Quoted(script, "After about twelve seconds");
        StringAssert.Contains(nothingHeard, "gone");
        StringAssert.Contains(nothingHeard, "still connected");
        Assert.IsFalse(nothingHeard.Contains("more than two minutes", StringComparison.Ordinal), "The figures do not wait two minutes to go: they go when the AirPods are not connected.");

        string anHour = Quoted(script, "More than an hour after the AirPods were last heard");
        StringAssert.Contains(anHour, "Not on this PC");
        StringAssert.Contains(anHour, "No recent reading");
    }

    [TestMethod]
    public void TheStepThatNeedsFreshFiguresAfterTheCaseWasShutConnectsTheAirPodsFirst()
    {
        string script = Script();

        string reopen = Quoted(script, "Open the case lid next to this computer and connect the AirPods");
        StringAssert.Contains(reopen, "circular arrow");
        Assert.IsLessThan(reopen.IndexOf("circular arrow", StringComparison.Ordinal), reopen.IndexOf("connect the AirPods", StringComparison.Ordinal), "Connect before the refresh.");

        string halfC = Quoted(script, "Connect the AirPods to this PC (left-click the Earshot icon or the gauge, then Connect). Then put both");
        StringAssert.Contains(halfC, "connect them again");
    }

    [TestMethod]
    public void AComparisonWithTheIPhoneSaysToAnswerNotSureWhenTheCardShowsNoFigure()
    {
        string compare = Quoted(Script(), "Compare the card with what your iPhone shows");

        StringAssert.Contains(compare, "no battery figure at all");
        StringAssert.Contains(compare, "not sure");
    }

    // The wording the test window shows carries the same sentences, keyed by the script's own text: a prompt changed in the
    // script and not there would be shown to the owner in the old plain words.
    [TestMethod]
    public void TheTestWindowsWordingHasEveryChangedPromptUnderItsScriptText()
    {
        string wording = File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));
        string script = Script();

        foreach (string start in new[] { "A minute later", "After about twelve seconds", "More than an hour after the AirPods were last heard", "Open the case lid next to this computer and connect the AirPods" })
        {
            string text = Quoted(script, start);
            string asJson = text.Replace("\"", "\\\"", StringComparison.Ordinal);
            StringAssert.Contains(wording, "\"scriptText\": \"" + asJson + "\"", "wording.json has no entry for: " + text);
        }
    }
}
