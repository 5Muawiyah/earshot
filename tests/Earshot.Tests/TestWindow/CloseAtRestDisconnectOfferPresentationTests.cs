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
// matches all three by exact text instead. This file pins the plain line each one falls back to,
// so a mismatch between LiveTest.psm1's own literal text and PromptPresenter's copy of it is
// caught here rather than only by the slower, real-run sweep in NoBannedWordsInPlainModeTests.
[TestClass]
public sealed class CloseAtRestDisconnectOfferPresentationTests
{
    private static readonly string[] AtRestStack = { "Confirm-Step", "Close-AtRest" };

    // Verbatim from tools\live-tests\LiveTest.psm1's $script:AtRestDisconnectConsequence.
    private const string DisconnectConsequence =
        "Disconnects the AirPods from this PC first, with the same one-shot disconnect a left click sends, " +
        "and reads the render endpoint again. Windows refuses to disable the A2DP sink entry while it is rendering " +
        "(CR_REMOVE_VETOED, 21 September 2026), so a block sent now would only partly take. Your AirPods will stop playing from this computer.";

    // Verbatim from tools\live-tests\LiveTest.psm1's $script:AtRestBlockWhilePlayingConsequence
    // ($script:AtRestDefaultConsequence plus its own addendum).
    private const string BlockWhilePlayingConsequence =
        "Blocks the AirPods Bluetooth nodes so this PC does not page them at the next boot. " +
        "If the AirPods are playing through this PC right now, that stops. The AirPods still read as playing from this PC, " +
        "so Windows may refuse the audio entry as it did on 21 September; the re-read afterwards decides.";

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

    [TestMethod]
    public void TheDisconnectOfferGetsItsOwnHeadingAndPlainLineWithNoTechnicalWord()
    {
        var bound = new Dictionary<string, string> { ["Consequence"] = DisconnectConsequence };
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
        var bound = new Dictionary<string, string> { ["Consequence"] = BlockWhilePlayingConsequence };
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
