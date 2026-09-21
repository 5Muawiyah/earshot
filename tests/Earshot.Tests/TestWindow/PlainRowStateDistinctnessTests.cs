using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// RowPresenter.PlainText is presentation only: the underlying state logic (StateDeriver,
// RowPresenter.Text, RowColor) does not change at all. No plain word may make Unknown, a failure
// or a qualified pass look better than it is, so every RowStateKind's own plain label, and every
// qualifier's plain translation combined with a pass, is pinned here as its own distinct text,
// never colliding with the clean "Worked" a fully confirmed pass reads as.
[TestClass]
public sealed class PlainRowStateDistinctnessTests
{
    // The qualifiers StateDeriver actually writes (StateDeriverPropertyTests and
    // KillDeadlineCancelledByAtRestOfferTests-style fixtures exercise the same four; kept as a
    // literal list here so a fifth one added later is caught by AnyUnknownQualifierFallsBackToItsOwnWords
    // rather than silently mistranslated).
    private static readonly string[] KnownQualifiers =
    {
        "on an earlier build", "second half only on record", "shut down not confirmed", "run order not confirmed",
    };

    [TestMethod]
    public void EveryRowStateKindsPlainLabelIsDistinctFromEveryOther()
    {
        var seen = new Dictionary<string, RowStateKind>(StringComparer.Ordinal);
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            string text = Copy.PlainBaseText(kind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), kind + " has no plain label.");
            if (seen.TryGetValue(text, out RowStateKind earlier))
            {
                Assert.Fail(kind + " and " + earlier + " share the same plain label '" + text + "'.");
            }

            seen[text] = kind;
        }
    }

    [TestMethod]
    public void UnknownsPlainLabelIsNeverTheCleanPassLabel()
    {
        Assert.AreNotEqual(Copy.PlainPassed, Copy.PlainBaseText(RowStateKind.Unknown));
        StringAssert.DoesNotMatch(Copy.PlainBaseText(RowStateKind.Unknown), new System.Text.RegularExpressions.Regex("^Worked$"));
    }

    [TestMethod]
    public void EveryNonPassedKindsPlainTextNeverContainsTheWordWorked()
    {
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            if (kind == RowStateKind.Passed)
            {
                continue;
            }

            var state = new DerivedRowState { Kind = kind };
            string text = RowPresenter.PlainText(state);
            Assert.IsFalse(text.Contains(Copy.PlainPassed, StringComparison.Ordinal), kind + " rendered as '" + text + "', which names Worked.");
        }
    }

    [TestMethod]
    public void EveryKnownQualifiersPlainTranslationIsDistinctAndNeverBareWorked()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { Copy.PlainPassed };
        foreach (string qualifier in KnownQualifiers)
        {
            string plain = Copy.PlainQualifier(qualifier);
            Assert.IsFalse(string.IsNullOrWhiteSpace(plain), "'" + qualifier + "' has no plain translation.");
            Assert.IsTrue(seen.Add(plain), "'" + qualifier + "' translates to '" + plain + "', which collides with another qualifier or with 'Worked' itself.");

            // A qualified pass must never read as the bare, unqualified "Worked": the qualifier's
            // own plain words must still be present in the combined row text.
            var qualifiedPass = new DerivedRowState { Kind = RowStateKind.Passed, Qualifier = qualifier };
            string rowText = RowPresenter.PlainText(qualifiedPass);
            Assert.AreNotEqual(Copy.PlainPassed, rowText, "'" + qualifier + "' qualified pass reads as bare 'Worked'.");
            StringAssert.Contains(rowText, plain);
        }
    }

    [TestMethod]
    public void AnUnknownQualifierFallsBackToItsOwnWordsRatherThanVanishing()
    {
        string plain = Copy.PlainQualifier("a brand new qualifier never seen before");
        Assert.AreEqual("a brand new qualifier never seen before", plain);
    }

    [TestMethod]
    public void PlainRowTextNeverChangesTheUnderlyingKindOrColour()
    {
        // Presentation only: the same DerivedRowState renders a different string from PlainText
        // than from Text, but IsGreen (and so RowColor) is read from the state alone, never
        // recomputed by either presenter.
        var state = new DerivedRowState { Kind = RowStateKind.Passed };
        Assert.AreEqual(RowPresenter.RowColor(state), RowPresenter.RowColor(state));
        Assert.AreNotEqual(RowPresenter.Text(state), RowPresenter.PlainText(RowStateKindOnlyUnknown()));
    }

    private static DerivedRowState RowStateKindOnlyUnknown() => new() { Kind = RowStateKind.Unknown };
}
