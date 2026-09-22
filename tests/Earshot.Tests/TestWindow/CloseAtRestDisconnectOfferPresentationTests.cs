using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The closing step's disconnect-first offer (Close-AtRest, LiveTest.psm1) sends two consequence
// texts PromptPresenter has never seen before this fix: $script:AtRestDisconnectConsequence
// (offered whenever render reads ACTIVE or cannot be read) and
// $script:AtRestBlockWhilePlayingConsequence (the block offer's own text when the disconnect did
// not confirm). Both are module-level PowerShell variables, never a literal any one script
// repeats, so neither can carry a wording.json entry under this project's own "scriptText must be
// in its own script" rule (WordingManifestTests.EveryWordingEntrysScriptTextIsInItsScript) -- the
// same reason the pre-existing CloseAtRestSharedConsequence has no entry either. PromptPresenter
// matches all three by exact text instead.
//
// This file pins the plain line each one falls back to, reading both texts out of
// tools\live-tests\LiveTest.psm1 itself (SelfTestFixtures.LoadOwnerTables, through
// Export-FakeOwnerTables.ps1) rather than repeating them as a second, hand-kept copy: a hand-kept
// copy would go on matching PromptPresenter.cs's own constant even after LiveTest.psm1's real text
// moved away from it, which is exactly the gap that let the two of them drift silently before this
// file read the module directly. So a mismatch between LiveTest.psm1's own text and
// PromptPresenter's copy of it is caught here, not only by the slower, real-run sweep in
// NoBannedWordsInPlainModeTests.
[TestClass]
public sealed class CloseAtRestDisconnectOfferPresentationTests
{
    private static readonly string[] AtRestStack = { "Confirm-Step", "Close-AtRest" };

    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    private static ChildMessage Prompt(IReadOnlyDictionary<string, string> bound) => new()
    {
        Kind = ChildMessageKind.Prompt,
        Seq = 1,
        Caller = "Confirm-Step",
        Prompt = "Run it now? [y/N]",
        Bound = bound,
        Stack = AtRestStack,
    };

    private static FakeOwnerTables LoadTables()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        return SelfTestFixtures.LoadOwnerTables(RepositoryLocator.RepositoryRoot(), host);
    }

    [TestMethod]
    public void TheDisconnectOfferGetsItsOwnHeadingAndPlainLineWithNoTechnicalWord()
    {
        FakeOwnerTables tables = LoadTables();
        var bound = new Dictionary<string, string> { ["Consequence"] = tables.CloseAtRestDisconnect };
        PresentedPrompt presented = PromptPresenter.Present(Prompt(bound), "01", LoadWording());

        Assert.AreEqual("Stop your AirPods playing from this computer?", presented.Heading);
        Assert.AreEqual(
            "Your AirPods will stop playing from this computer, so that the next step can block them.",
            presented.PlainLine);

        // The raw consequence stays under "What it does:", technical, matching every other
        // Confirm-Step prompt; only the plain line above is what a plain-mode reader ever sees.
        Assert.IsTrue(presented.DetailIsTechnical);
        StringAssert.Contains(presented.DetailText, "A2DP");
        foreach (string word in new[] { "A2DP", "endpoint", "one-shot", "CR_REMOVE_VETOED", "this PC" })
        {
            Assert.DoesNotContain(word, presented.PlainLine);
        }
    }

    [TestMethod]
    public void TheBlockWhilePlayingOfferKeepsTheCloseAtRestHeadingAndGetsItsOwnPlainLine()
    {
        FakeOwnerTables tables = LoadTables();
        var bound = new Dictionary<string, string> { ["Consequence"] = tables.CloseAtRestBlockWhilePlaying };
        PresentedPrompt presented = PromptPresenter.Present(Prompt(bound), "01", LoadWording());

        Assert.AreEqual("Stop this computer grabbing your AirPods again?", presented.Heading);
        Assert.AreEqual(
            "This computer will try to block your AirPods now. Because they may still be playing here, Windows may only " +
            "let part of it happen; the record will say.",
            presented.PlainLine);

        Assert.IsTrue(presented.DetailIsTechnical);
        foreach (string word in new[] { "A2DP", "endpoint", "one-shot", "CR_REMOVE_VETOED", "this PC" })
        {
            Assert.DoesNotContain(word, presented.PlainLine);
        }
    }
}
