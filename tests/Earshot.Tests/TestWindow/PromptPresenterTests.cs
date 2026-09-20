using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// PromptPresenter turns one protocol prompt message into what StepPanel shows (test-gui.md
// section 7.1's table), decided here rather than in the form.
[TestClass]
public sealed class PromptPresenterTests
{
    private static readonly string[] ExpectedPreconditions = { "Set up", "Nodes allowed" };
    private static readonly string[] ExpectedPhysicalActions = { "Keep the phone nearby" };
    private static readonly string[] AtRestStack = { "Confirm-Step", "Close-AtRest" };
    private static readonly string[] YesNoNotSureLabels = { "Yes", "No", "Not sure" };
    private static readonly string[] YesNoUnsureReplies = { "yes", "no", "unsure" };
    private static readonly string[] OnOffLeaveLabels = { "On, as Earshot ships", "Off", "Leave it as it is" };
    private static readonly string[] OnOffLeaveReplies = { "on", "off", "leave" };
    private static readonly string[] CardNoteLabels = { "Connected", "Disconnected", "Something else", "I saw no card" };
    private static readonly string[] Test14TranscriptWithTwoAddresses =
    {
        "Pinned address 0A1B2C3D4E5F, container x, state Blocked",
        "Addresses seen in the Bluetooth node list, other than the pinned one:",
        "  1A2B3C4D5E6F",
        "  6F5E4D3C2B1A",
        "Windows Bluetooth settings shows which device each one is, under the device properties.",
    };
    private static readonly string[] Test14TranscriptWithNoAddresses = { "Addresses seen in the Bluetooth node list, other than the pinned one:" };
    private static readonly string[] Test14ExpectedButtonLabels = { "1A2B3C4D5E6F", "6F5E4D3C2B1A", "None of these is my phone" };

    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    private static ChildMessage Prompt(string caller, string prompt, IReadOnlyDictionary<string, string> bound, IReadOnlyList<string>? stack = null) => new()
    {
        Kind = ChildMessageKind.Prompt,
        Seq = 1,
        Caller = caller,
        Prompt = prompt,
        Bound = bound,
        Stack = stack ?? new[] { caller },
    };

    [TestMethod]
    public void ShowPreconditionsListsPreconditionsAndPhysicalActionsVerbatim()
    {
        var bound = new Dictionary<string, string>
        {
            ["Preconditions"] = "[\"Set up\",\"Nodes allowed\"]",
            ["PhysicalActions"] = "[\"Keep the phone nearby\"]",
        };

        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("Show-Preconditions", "Are all of those true, and are you ready to start? [y/N]", bound), "01", LoadWording());

        Assert.AreEqual("Before this test starts", presented.Heading);
        CollectionAssert.AreEqual(ExpectedPreconditions, presented.ListItems.ToArray());
        CollectionAssert.AreEqual(ExpectedPhysicalActions, presented.PhysicalActions.ToArray());
        Assert.AreEqual(2, presented.Buttons.Count);
        Assert.AreEqual("y", presented.Buttons[0].Reply);
        Assert.AreEqual("n", presented.Buttons[1].Reply);
    }

    [TestMethod]
    public void ConfirmStepLooksUpThePlainLineByItsConsequence()
    {
        var bound = new Dictionary<string, string>
        {
            ["Consequence"] = "Sends KSPROPERTY_ONESHOT_RECONNECT to the A2DP filter only. If it works, the AirPods leave your phone and join this PC.",
        };

        PresentedPrompt presented = PromptPresenter.Present(Prompt("Confirm-Step", "Run it now? [y/N]", bound), "01", LoadWording());

        Assert.AreEqual("Asks the AirPods to connect to this PC. If it works, the sound moves from your phone to this PC.", presented.PlainLine);
        StringAssert.Contains(presented.DetailText, "KSPROPERTY_ONESHOT_RECONNECT");
    }

    [TestMethod]
    public void ConfirmStepWithNoWordingEntryFallsBackToTheScriptsOwnWords()
    {
        var bound = new Dictionary<string, string> { ["Consequence"] = "A brand new consequence never seen before." };
        PresentedPrompt presented = PromptPresenter.Present(Prompt("Confirm-Step", "Run it now? [y/N]", bound), "01", LoadWording());
        Assert.AreEqual("A brand new consequence never seen before.", presented.PlainLine);
    }

    [TestMethod]
    public void CloseAtRestConfirmStepGetsItsOwnHeading()
    {
        var bound = new Dictionary<string, string> { ["Consequence"] = "Blocks the AirPods Bluetooth nodes." };
        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("Confirm-Step", "Run it now? [y/N]", bound, AtRestStack), "01", LoadWording());
        Assert.AreEqual("Put this PC back at rest?", presented.Heading);
    }

    [TestMethod]
    public void ReadAnswerOffersYesNoAndNotSureByDefault()
    {
        var bound = new Dictionary<string, string> { ["Question"] = "Did the audio move from your phone to this PC?" };
        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("Read-Answer", "  [yes/no/unsure]", bound), "01", LoadWording());

        Assert.AreEqual("Did the sound move from your phone to this PC?", presented.PlainLine);
        CollectionAssert.AreEqual(YesNoNotSureLabels, presented.Buttons.Select(b => b.Label).ToArray());
        CollectionAssert.AreEqual(YesNoUnsureReplies, presented.Buttons.Select(b => b.Reply).ToArray());
    }

    [TestMethod]
    public void ReadAnswerWithOnOffOptionsUsesTheFixedLabelsFromTheBoundOptions()
    {
        var bound = new Dictionary<string, string>
        {
            ["Question"] = "Which state do you want to finish in?",
            ["Options"] = "[\"on\",\"off\",\"leave\"]",
        };

        PresentedPrompt presented = PromptPresenter.Present(Prompt("Read-Answer", "  [on/off/leave]", bound), "00", LoadWording());
        CollectionAssert.AreEqual(OnOffLeaveLabels, presented.Buttons.Select(b => b.Label).ToArray());
        CollectionAssert.AreEqual(OnOffLeaveReplies, presented.Buttons.Select(b => b.Reply).ToArray());
    }

    [TestMethod]
    public void ReadNoteUsesTheStaticChoiceButtonsFromWordingJson()
    {
        var bound = new Dictionary<string, string> { ["Question"] = "What did the card near the tray say, word for word?" };
        PresentedPrompt presented = PromptPresenter.Present(Prompt("Read-Note", "  Your answer (Enter to leave it blank)", bound), "08", LoadWording());

        Assert.AreEqual("Which did the card say?", presented.PlainLine);
        CollectionAssert.AreEqual(
            CardNoteLabels,
            presented.Buttons.Select(b => b.Label).ToArray());
        Assert.AreEqual("No card seen", presented.Buttons[3].Reply);
    }

    // section 7.3, test 14: no static choices are written for the phone-address note because the
    // script prints the addresses at run time; one button per address seen in the transcript,
    // plus "None of these is my phone".
    [TestMethod]
    public void ReadNoteForTest14BuildsButtonsFromTheAddressesPrintedInTheTranscript()
    {
        var bound = new Dictionary<string, string> { ["Question"] = "Type the twelve character address of your phone, upper case." };

        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("Read-Note", "  Your answer (Enter to leave it blank)", bound), "14", LoadWording(), Test14TranscriptWithTwoAddresses);

        Assert.AreEqual("Which of these addresses is your phone?", presented.PlainLine);
        CollectionAssert.AreEqual(Test14ExpectedButtonLabels, presented.Buttons.Select(b => b.Label).ToArray());
        Assert.AreEqual("1A2B3C4D5E6F", presented.Buttons[0].Reply);
        Assert.AreEqual(string.Empty, presented.Buttons[2].Reply);
    }

    [TestMethod]
    public void ReadNoteForTest14WithNoAddressLinesOffersOnlyTheLastButton()
    {
        var bound = new Dictionary<string, string> { ["Question"] = "Type the twelve character address of your phone, upper case." };

        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("Read-Note", "  Your answer (Enter to leave it blank)", bound), "14", LoadWording(), Test14TranscriptWithNoAddresses);

        Assert.AreEqual(1, presented.Buttons.Count);
        Assert.AreEqual("None of these is my phone", presented.Buttons[0].Label);
    }

    [TestMethod]
    public void WaitOwnerYesSendsAnEmptyLineAndNoSendsNothing()
    {
        var bound = new Dictionary<string, string> { ["Text"] = "Connect the AirPods back to this PC from Windows Bluetooth settings, and wait until sound plays from this PC again." };
        PresentedPrompt presented = PromptPresenter.Present(Prompt("Wait-Owner", "  Press Enter when it is done", bound), "02", LoadWording());

        Assert.AreEqual("Do this:", presented.Heading);
        Assert.IsTrue(presented.Buttons[0].SendsReply);
        Assert.AreEqual(string.Empty, presented.Buttons[0].Reply);
        Assert.IsFalse(presented.Buttons[1].SendsReply);
    }

    [TestMethod]
    public void AnUnknownCallerOffersNoButtonsAndIsMarkedStopOnly()
    {
        PresentedPrompt presented = PromptPresenter.Present(
            Prompt("SomeOtherFunction", "A raw prompt nobody wraps", new Dictionary<string, string>()), "01", LoadWording());

        Assert.IsTrue(presented.StopOnly);
        Assert.AreEqual(0, presented.Buttons.Count);
        Assert.AreEqual("A raw prompt nobody wraps", presented.PlainLine);
    }
}
