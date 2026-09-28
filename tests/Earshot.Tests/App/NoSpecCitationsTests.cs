using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The window's own NoDocumentCitationsTests (tests\Earshot.Tests\TestWindow) keeps that window's three
// folders free of a reference to a design document or a review round that lives outside this repository.
// This is the same idea over the other two trees a reviewer actually reads: src\Earshot itself and the
// whole of tests\Earshot.Tests, TestWindow included, so the two checks overlap there on purpose rather
// than leaving a seam between them. A citation reads fine while the document sits open beside the code and
// like a locked door the moment it does not: everything under these two trees has to say what the code
// does and why in its own terms.
[TestClass]
public sealed class NoSpecCitationsTests
{
    // The label shape used across every review round so far, B1, B2, M1 to M6, m1 to m12, S1 to S8 (the
    // live-test samples), T1 to T15, D2 to D8 (design-document decisions) and L6, L17 (design addenda): one
    // of those letters immediately followed by one or two digits, then either a colon, a possessive ("D2's
    // order", "S8's own acceptance line"), "says" ("D8 says nothing about this site may be faked"), or "in"
    // ("D8 in the widget's own design notes": the widget-data review's own escape, the label pointing
    // straight at the document that holds it without a colon or a possessive). The letter set is the
    // project's own label alphabet, not every letter, so this never flags an architecture reference such as
    // "on x64:" (BluetoothApis.cs), and the trailing anchor is what keeps it from flagging a .NET format
    // string such as ("D2", CultureInfo.InvariantCulture): the characters right after "D2" there are a
    // closing quote and a comma, never one of the anchors this looks for. "section N" and "N.N" cover a
    // design document's own numbering; "review round" and the two project slugs cover the documents by name.
    // "the spec" is deliberately not one of these: it is also the generic term other, unrelated test suites
    // in this repository use for their own external acceptance specifications (Hotkeys, Voice), and banning
    // it here would flag those, not a widget-data.md citation.
    // Case-insensitive: "Section 9" at the start of a sentence is exactly as much a citation as "section 9".
    // The PascalCase lookahead is the one part kept case-sensitive on purpose ((?-i:...)): under IgnoreCase,
    // [A-Z][a-z] would stop meaning "an upper-case letter starting a new word" and start meaning "any two
    // letters", turning every hex GUID's own run of letters and digits (B3AB, and worse) into a false hit.
    // Round 2: this missed every label written the way a reviewer's own shorthand actually reads: "(D2)",
    // "(M5)", "(F6)", a semicolon or comma joining a second label or a trailing note such as
    // "(M4; acceptance 17)" or "(D6, B1)". Rather than widen the existing colon/possessive/says/names/in
    // anchors (a comma or closing paren there would start matching ordinary code too, a local timestamp
    // variable such as "t0" passed to a helper, "Utc(t0)", being the clearest example once the whole regex's
    // own IgnoreCase option is remembered), a new alternative looks for the shape those citations are
    // actually written in: an opening parenthesis immediately before the label. It stays case-sensitive on
    // purpose ((?-i:...), the same device the PascalCase lookahead below already uses) for exactly that
    // reason: "t0" and "d2" are ordinary lowercase identifiers, but a citation's own letter is always
    // written upper-case. "F" only appears here, not in the older, unanchored alternative below, since
    // unanchored it would start matching hex GUID fragments (CfgMgr32.cs and friends are full of them).
    //
    // A bare two-part decimal in parentheses, such as the review's own "(12.3)" and "(2.4)", was tried the
    // same way and dropped again: "(1.5)" is also exactly how an ordinary TimeSpan.FromSeconds argument
    // reads, and nothing about the text alone tells the two apart. Those two exact citations are removed by
    // hand instead, in every file this document owns.
    // Third return of this class: "spec 7.6" and "7.3's" cite a design document's own section numbering
    // directly, with no letter prefix at all, so none of the letter-code alternatives above ever had a
    // chance of matching either shape; "(see the report)" and "the design narrative" are literal phrases
    // pointing at a document outside this repository rather than a label, so they need their own literal
    // alternatives too. "spec" alone stays unbanned (the class comment on the first version of this field
    // explains why), so only "spec" directly followed by a number is new here.
    // Fourth return of this class: every alternative above reads a review's own label shape (a letter code,
    // a decimal section number, a named document); none of them ever covered the plainest shape of all, the
    // fix list's own numbering read straight off the page - "item 1", "Item 2's second half", "Round 2 item
    // 4" - because it has no letter, no decimal point and no document name for any prior alternative to
    // anchor on, just an ordinary English word immediately before a bare number. Two new alternatives, each
    // exactly that shape and nothing wider: "item" or "round" (either case), a space, then one or more
    // digits. Deliberately still not "spec" or "section" alone, for the same reason the class comment above
    // gives for those; deliberately not "round" or "item" without a following number either, since both are
    // ordinary English words everywhere else in this codebase (a round trip, a loop variable named item) and
    // only the pairing with a bare number is unique to a citation.
    private static readonly Regex Citation = new(
        @"\bsection [0-9]+(\.[0-9]+)?\b|\b[BDHLMmST][0-9]{1,2}\b(?::|'s\b| says\b| names\b| the other half\b| in\b)|\b[BDHLMmST][0-9]{1,2}(?=(?-i:[A-Z][a-z]))|\((?-i:[BDFHLMST])[0-9]{1,2}\b|review round|handback-on-shutdown-and-sleep|handback-review|\bspec [0-9]+(\.[0-9]+)?\b|\b[0-9]+\.[0-9]+'s\b|\(see the report\)|the design narrative|\bitem [0-9]+\b|\bround [0-9]+\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] OwnedFolders =
    {
        Path.Combine("src", "Earshot"),
        Path.Combine("tests", "Earshot.Tests"),
    };

    // This file's own path, captured at compile time, the same trick NoDocumentCitationsTests uses: the
    // pattern has to appear literally in the regex above, so this one file, and only this one, is excluded
    // from the scan below, not because its comments are special but because the check's own source is not a
    // citation of anything. TestWindow's own NoDocumentCitationsTests.cs is scanned like every other file:
    // its own example labels are written so they do not themselves match (see its own comments).
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // Round 2: the regex missed every one of these, all drawn from the review's own list of citations it
    // could not tell apart from ordinary code. "(12.3)" and "(2.4)" are not here: see the class comment on
    // Citation for why that particular shape was tried and dropped again.
    [TestMethod]
    [DataRow("wire up a different table (M4; acceptance 17).")]
    [DataRow("the occupied rectangles (D2), plus the")]
    [DataRow("touched from anywhere else (F6).")]
    [DataRow("for one candidate (D6, B1). It no longer does")]
    [DataRow("the real watcher is Passive (M5), rather than")]
    public void CitationCatchesEveryLabelShapeTheReviewListed(string plantedLine)
    {
        Assert.IsTrue(Citation.IsMatch(plantedLine), "Missed: " + plantedLine);
    }

    // Third return of this class: these four shapes were live in the tree at the head this round started
    // from (CaseOpenCardPresenter.cs, WidgetCard.cs, WidgetCardPresenter.cs, WidgetCardPlacement.cs,
    // WidgetCopy.cs, TrayContext.Widget.cs, GaugeController.cs, UiaTaskbarReader.cs, ProbeWidget.cs and
    // CaseOpenCardTests.cs all carried at least one), and none of them matched the regex as it stood then:
    // a bare section number with no letter prefix, or a phrase pointing at a document rather than a label.
    [TestMethod]
    [DataRow("checked in the order spec 7.6 gives them")]
    [DataRow("matching 7.3's painting model, rather than")]
    [DataRow("a larger change than this step's scope (see the report). This gives")]
    [DataRow("rather than the exact free-run point the design narrative describes.")]
    public void CitationCatchesEveryShapeMissedByTheThirdReviewRound(string plantedLine)
    {
        Assert.IsTrue(Citation.IsMatch(plantedLine), "Missed: " + plantedLine);
    }

    // Fourth return of this class: these were live in the tree at the head this round started from
    // (WidgetStatusService.cs, WidgetStatusServiceTests.cs, WidgetStatusServiceRealWatcherBindingTests.cs,
    // TrayStreamingTests.cs, HandForgedSequenceMarkerTests.cs, ClaimStoreTests.cs, OwnershipRuleTests.cs),
    // and none of them matched the regex as it stood then: an ordinary English word immediately before a
    // bare number, with no letter code and no document name in sight.
    [TestMethod]
    [DataRow("against a Stopped event's own Generation (item 1) to tell a run's genuine end")]
    [DataRow("suspended must not start the watcher again until Resume() itself does (item 2's second half).")]
    [DataRow("Item 2: a failed start's own error must stand, not be discarded")]
    [DataRow("Item 2 of the review. Switching the whole feature off")]
    [DataRow("Round 2 item 4: a claim file written before NibblesAreNamedOrder existed")]
    [DataRow("Round 2, owner decision 2026-09-27 (\"same checks always\"): a live connection used to waive")]
    public void CitationCatchesEveryShapeMissedInTheFourthReviewRound(string plantedLine)
    {
        Assert.IsTrue(Citation.IsMatch(plantedLine), "Missed: " + plantedLine);
    }

    // Confirms the new pairing does not also start flagging the two ordinary English words it is built
    // from, the moment neither is followed by a bare number: a loop variable named item, and an actual
    // round trip.
    [TestMethod]
    [DataRow("foreach (var item in collection) { Process(item); }")]
    [DataRow("a round trip to the service and back, not a cached read")]
    public void AnOrdinaryUseOfItemOrRoundWithNoFollowingNumberIsNotMistakenForACitation(string plantedLine)
    {
        Assert.IsFalse(Citation.IsMatch(plantedLine), "False positive: " + plantedLine);
    }

    // The two cases the letter-based fix above cannot cover, without also flagging a plain decimal argument
    // such as TimeSpan.FromSeconds(1.5): confirms the ordinary case still reads clean, so the gap is only
    // ever a bare number in parentheses, never a real citation shape.
    [TestMethod]
    public void AnOrdinaryDecimalArgumentInParenthesesIsNotMistakenForACitation()
    {
        Assert.IsFalse(Citation.IsMatch("time.Advance(TimeSpan.FromSeconds(1.5));"));
    }

    // Case sensitivity on the new alternative matters: with the whole regex's own IgnoreCase in force, an
    // ordinary lowercase timestamp variable passed to a helper must not read as an upper-case citation label.
    [TestMethod]
    [DataRow("string s = Prefix(trigger) + \"started at \" + Utc(t0) + \" (\" + flagsOrReason;")]
    [DataRow("TimeSpan disconnectRemaining = Remaining(t0 + disconnectWait);")]
    public void ALowercaseTimestampVariableInParenthesesIsNotMistakenForACitation(string plantedLine)
    {
        Assert.IsFalse(Citation.IsMatch(plantedLine), "False positive: " + plantedLine);
    }

    [TestMethod]
    public void NoCommentOrTestNameCitesADesignDocumentOrAReviewRound()
    {
        string root = RepositoryLocator.RepositoryRoot();
        string thisFile = Path.GetFullPath(ThisFilePath());
        var found = new List<string>();
        int filesRead = 0;

        foreach (string folder in OwnedFolders)
        {
            string full = Path.Combine(root, folder);
            Assert.IsTrue(Directory.Exists(full), "Owned folder not found: " + full);

            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filesRead++;

                string fileName = Path.GetFileName(file);
                if (Citation.IsMatch(fileName))
                {
                    found.Add(Path.GetRelativePath(root, file) + ": file name itself: " + fileName);
                }

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Citation.IsMatch(lines[i]))
                    {
                        found.Add(Path.GetRelativePath(root, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                    }
                }
            }
        }

        Assert.IsTrue(filesRead >= 300, "Only " + filesRead + " files were read across " + string.Join(", ", OwnedFolders) + ", so a clean result would prove nothing.");
        Assert.AreEqual(0, found.Count, "A comment or name still cites a document that is not in this repository:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }
}
