using Earshot.Tests.TestWindow;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The architecture notes say whether the Apple seed for the buds' charge rate is on. They must say what the code does.
[TestClass]
public sealed class ChargeRatesDocsTests
{
    [TestMethod]
    public void TheArchitectureNotesSayTheAppleSeedIsOnAndConfirmedWhenTheCodeHasItOn()
    {
        string notes = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), "docs", "architecture.md")), @"\s+", " ");

        bool notesSayOn = notes.Contains("confirmed against that page on 12 October 2026, and the seed is on (`ChargeRates.UseAppleBudSeed`)", StringComparison.Ordinal);
        Assert.AreEqual(ChargeRates.UseAppleBudSeed, notesSayOn, "The notes and the code disagree about whether the seed is on.");
        Assert.IsFalse(notes.Contains("the seed is off", StringComparison.OrdinalIgnoreCase), "The notes still say the seed is off.");
        Assert.IsFalse(notes.Contains("could not be fetched here and is unconfirmed", StringComparison.Ordinal), "The notes still call the wording unconfirmed.");
    }
}
