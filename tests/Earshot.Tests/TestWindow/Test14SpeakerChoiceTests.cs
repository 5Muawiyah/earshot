using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The window can never type -SpeakerAddress in for test 14 (no text box anywhere in this
// project), so it is chosen from buttons instead, built from whatever an earlier run's own node
// evidence already shows (SpeakerCandidateFinder). Real MainForm, real row selection, exactly the
// way every other *ForTests test in this project drives it.
[TestClass]
public sealed class Test14SpeakerChoiceTests
{
    [TestMethod]
    public void WithNoEarlierNodeEvidenceTheRowSaysSoAndOffersNoButtons()
    {
        using var sandbox = new Earshot.Tests.TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("14"));
            Assert.AreEqual(0, form.SpeakerAddressChoicesForTests.Count);
            StringAssert.Contains(form.RowDetailTextForTests, Copy.SpeakerAddressNoCandidates);
        });
    }

    [TestMethod]
    public void WithAnEarlierRunsNodeEvidenceTheRowOffersOneButtonPerCandidatePlusNoSecondDevice()
    {
        using var sandbox = new Earshot.Tests.TempFolder();
        WriteNodeEvidence(sandbox.Path, "B4C5D6E7F809", "C7D8E9F0A1B2");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("14"));
            Assert.IsFalse(form.RowDetailTextForTests.Contains(Copy.SpeakerAddressNoCandidates, StringComparison.Ordinal));

            IReadOnlyList<string> choices = form.SpeakerAddressChoicesForTests;
            Assert.AreEqual(3, choices.Count, "two candidates plus \"" + Copy.SpeakerAddressChoiceNone + "\": " + string.Join(", ", choices));
            CollectionAssert.Contains(choices.ToArray(), "B4C5D6E7F809");
            CollectionAssert.Contains(choices.ToArray(), "C7D8E9F0A1B2");
            CollectionAssert.Contains(choices.ToArray(), Copy.SpeakerAddressChoiceNone);
        });
    }

    [TestMethod]
    public void ClickingACandidateButtonRecordsItAndClickingNoSecondDeviceClearsIt()
    {
        using var sandbox = new Earshot.Tests.TempFolder();
        WriteNodeEvidence(sandbox.Path, "B4C5D6E7F809");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("14"));
            Assert.IsNull(form.ChosenSpeakerAddressForTests, "nothing was chosen yet.");

            form.ClickSpeakerAddressChoiceForTests(0);
            Assert.AreEqual("B4C5D6E7F809", form.ChosenSpeakerAddressForTests);

            // The last button is always "No second device".
            form.ClickSpeakerAddressChoiceForTests(form.SpeakerAddressChoicesForTests.Count - 1);
            Assert.IsNull(form.ChosenSpeakerAddressForTests);
        });
    }

    [TestMethod]
    public void NoOtherRowShowsAnySpeakerAddressChoice()
    {
        using var sandbox = new Earshot.Tests.TempFolder();
        WriteNodeEvidence(sandbox.Path, "B4C5D6E7F809");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            Assert.AreEqual(0, form.SpeakerAddressChoicesForTests.Count);
        });
    }

    private static void WriteNodeEvidence(string sandboxFolder, params string[] otherAddresses)
    {
        string testFolder = Path.Combine(sandboxFolder, "local", "Earshot", "livetest", "20260921T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(testFolder);

        var nodes = new List<string>
        {
            // The pinned device's own node: same address twice, either side of "&0&", the shape a
            // per-service BTHENUM child node carries (never offered back as a candidate).
            "{ \"instanceId\": \"BTHENUM\\\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001005D\\\\8&0A1B2C3D4E8C&0&0A1B2C3D4E8C_C00000000\" }",
        };
        foreach (string address in otherAddresses)
        {
            nodes.Add("{ \"instanceId\": \"BTHENUM\\\\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\\\\7&" + address + "&0&" + address + "_C00000000\" }");
        }

        string json = "{ \"address\": \"0A1B2C3D4E8C\", \"nodeState\": \"Allowed\", \"nodes\": [" + string.Join(",", nodes) + "] }";
        File.WriteAllText(Path.Combine(testFolder, "nodes-before.json"), json);
    }
}
