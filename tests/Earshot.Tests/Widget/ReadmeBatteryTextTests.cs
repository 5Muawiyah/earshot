using System.Text.RegularExpressions;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The README's battery section says what the code does. Finding the pair again with no case open is built and switched
// off, so the text must say that, state its risk plainly, and not put the owner's acceptance on a thing he did not decide;
// the broadcast gives steps of 10%, so the text says a figure can differ from the iPhone's by up to one step.
[TestClass]
public sealed class ReadmeBatteryTextTests
{
    private static string Battery()
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        int start = text.IndexOf("\n## Battery", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, start, "The README has no Battery section.");
        int end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return Normalise(end < 0 ? text[start..] : text[start..end]);
    }

    // The README is wrapped by hand: a sentence is compared with its line breaks as single spaces.
    private static string Normalise(string text) => Regex.Replace(text, @"\s+", " ");

    // The section's bullets, each starting at its bold lead-in. Taken from the normalised text, where a bullet is
    // introduced by " - **".
    private static List<string> Bullets(string battery)
    {
        string[] parts = battery.Split(" - **");
        Assert.IsGreaterThan(1, parts.Length, "The Battery section has no bullets.");
        return parts.Skip(1).Select(p => "- **" + p).ToList();
    }

    [TestMethod]
    public void TheTextAgreesWithTheCodeThatFindingThePairAgainIsOff()
    {
        Assert.IsFalse(BroadcastRules.RefindEnabled, "The README says it is off.");
        Assert.IsFalse(new BroadcastSelector().RefindEnabled);
    }

    [TestMethod]
    public void FindingThePairAgainIsSaidToBeBuiltAndOffWithItsRiskStatedPlainly()
    {
        string bullet = Bullets(Battery()).Single(b => b.StartsWith("- **Finding your pair again without a case open is built and off", StringComparison.Ordinal));

        StringAssert.Contains(bullet, "It is off until the owner chooses");
        StringAssert.Contains(bullet, "after a link made while the buds were charging, or after a few hours");
        StringAssert.Contains(bullet, "any pair of that model and colour alone near the PC would qualify");
        Assert.IsFalse(bullet.Contains("I accept", StringComparison.Ordinal), "The owner did not decide this one.");
        Assert.IsFalse(Regex.IsMatch(bullet, @"\b(I|my|mine)\b"), "No owner voice for what he did not decide.");
    }

    [TestMethod]
    public void TheOwnersAcceptedRiskIsAboutLinkingAndFollowingAndNotAboutFindingThePairAgain()
    {
        string battery = Battery();
        string risk = Bullets(battery).Single(b => b.StartsWith("- **The risk that remains.**", StringComparison.Ordinal));

        StringAssert.Contains(risk, "I accept that risk");
        Assert.IsFalse(risk.Contains("found again", StringComparison.OrdinalIgnoreCase), "Finding the pair again is not part of what he accepted.");
        Assert.IsFalse(risk.Contains("without opening the case", StringComparison.OrdinalIgnoreCase));

        string which = Bullets(battery).Single(b => b.StartsWith("- **Which AirPods.**", StringComparison.Ordinal));
        Assert.IsFalse(which.Contains("found again", StringComparison.OrdinalIgnoreCase), "It is described in its own bullet, as off.");
    }

    [TestMethod]
    public void TheBroadcastIsSaidToGiveStepsOfTenPercentAndTheFigureToDifferByUpToOneStep()
    {
        string battery = Battery();

        StringAssert.Contains(battery, "steps of 10%");
        StringAssert.Contains(battery, "can differ from the one your iPhone shows by up to one step");
        Assert.IsFalse(Regex.IsMatch(battery, @"up to 9 points|9 points away", RegexOptions.IgnoreCase), "No claim about how far a figure is from the exact one.");
    }

    [TestMethod]
    public void EstimatesAreSaidToBeForAPairAwayFromThisPcOnly()
    {
        string text = Normalise(File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")));
        string battery = Battery();

        StringAssert.Contains(text, "While the AirPods are away from this PC, a part that was charging is shown rising as an estimate");
        StringAssert.Contains(battery, "shown only while the AirPods are away from this PC");
        string estimates = Bullets(battery).Single(b => b.StartsWith("- **Estimates are estimates.**", StringComparison.Ordinal));
        StringAssert.Contains(estimates, "With the AirPods on this PC the last reading is shown as it was read, never grown");
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
