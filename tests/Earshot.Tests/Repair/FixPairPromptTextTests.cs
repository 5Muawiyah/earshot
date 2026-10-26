using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The words of the pick and the pin dialogs. Built as data, so no window is shown.
[TestClass]
public sealed class FixPairPromptTextTests
{
    private static readonly string[] Rows = ["AirPods Pro", "Headphones with no name", "Headphones with no name"];

    // The scan shows headphones that can be paired and are in range; it cannot see pairing mode, so the line does not claim it.
    [TestMethod]
    public void ThePickSaysOnlyWhatTheScanSaw()
    {
        FixPrompt prompt = FixRePairWindow.PickPrompt([new UnpairedDevice("a", "AirPods Pro", "AABBCCDDEE01", true, 6, true)]);

        Assert.AreEqual("These Bluetooth headphones are nearby and can be paired. Choose your AirPods, then press Pair. If none is yours, cancel.", prompt.Body);
        Assert.IsFalse(prompt.Body.Contains("pairing mode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThePickListsEveryCandidateByNameOrAsHeadphonesWithNoNameAndChoosesNone()
    {
        UnpairedDevice[] found =
        [
            new("a", "AirPods Pro", "AABBCCDDEE01", true, 6, true),
            new("b", "", "AABBCCDDEE02", true, 6, true),
            new("c", "  ", "AABBCCDDEE03", true, 1, true),
        ];

        FixPrompt prompt = FixRePairWindow.PickPrompt(found);

        Assert.AreEqual("Choose your AirPods", prompt.Heading);
        CollectionAssert.AreEqual(Rows, prompt.Choices.ToArray());
        Assert.AreEqual("Pair", prompt.OkText);
    }

    [TestMethod]
    public void ThePinQuestionAsksWhetherTheDeviceShowsTheNumber()
    {
        FixPrompt match = FixRePairWindow.PinPrompt(FixPairKind.ConfirmPinMatch, "123456");
        FixPrompt display = FixRePairWindow.PinPrompt(FixPairKind.DisplayPin, "654321");

        Assert.AreEqual("Does your device show 123456?", match.Heading);
        Assert.AreEqual("Yes", match.OkText);
        Assert.Contains("654321", display.Heading, StringComparison.Ordinal);
    }

    [TestMethod]
    public void NoPromptOfThePairingUsesAnEmDash()
    {
        UnpairedDevice[] one = [new("a", "x", "AABBCCDDEE01", true, 6, true)];
        foreach (FixPrompt prompt in new[] { FixRePairWindow.PickPrompt(one), FixRePairWindow.PinPrompt(FixPairKind.ConfirmPinMatch, "1"), FixRePairWindow.PinPrompt(FixPairKind.DisplayPin, "1") })
        {
            Assert.IsFalse((prompt.Heading + prompt.Body + prompt.OkText).Contains((char)0x2014));
        }
    }
}
