using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The words of the pick and the pin dialogs. Built as data, so no window is shown.
[TestClass]
public sealed class FixPairPromptTextTests
{
    private static readonly string[] Rows = ["AirPods Pro", "Headphones with no name", "Headphones with no name"];

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
