using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// 14-SetDeviceRefusal.ps1's own "Moving the pin off a protected device" half needs a second
// device's address, and the window can never type one in for it (no text box anywhere in this
// project). SpeakerCandidateFinder reads it instead, from the same shape of file any earlier run's
// own node probe already wrote straight into its run folder (LiveTest.psm1's Get-NodeState:
// "<label>.json" beside that run's result.json), the same "nodes" array of {instanceId, ...}
// entries 14-SetDeviceRefusal.ps1's own Get-OtherAddresses parses. Nothing here touches a device:
// it only reads files a past run already left on disk.
[TestClass]
public sealed class SpeakerCandidateFinderTests
{
    private static readonly string[] TwoAddresses = { "B4C5D6E7F809", "C7D8E9F0A1B2" };
    private static readonly string[] NewestAddressOnly = { "BBBBBBBBBBBB" };

    [TestMethod]
    public void FindsNoCandidatesUnderAnEmptyOrMissingRoot()
    {
        using var folder = new Earshot.Tests.TempFolder();
        Assert.AreEqual(0, SpeakerCandidateFinder.Find(folder.Path).Count);
        Assert.AreEqual(0, SpeakerCandidateFinder.Find(Path.Combine(folder.Path, "does-not-exist")).Count);
    }

    [TestMethod]
    public void ReadsAddressesOutOfAnEarlierRunsOwnNodeFileExcludingThePinnedOne()
    {
        using var folder = new Earshot.Tests.TempFolder();
        string testFolder = Path.Combine(folder.Path, "20260921T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(testFolder);

        // The exact shape Get-NodeState/Fakes.psm1's Get-FakeNodes write: "address" is the pinned
        // device (excluded), and every instanceId in "nodes" carries a twelve hex character
        // address, the pinned one included once (which must not come back as a candidate).
        File.WriteAllText(Path.Combine(testFolder, "nodes-before.json"), """
            {
              "address": "0A1B2C3D4E8C",
              "nodeState": "Allowed",
              "nodes": [
                { "instanceId": "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001005D\\8&0A1B2C3D4E8C&0&0A1B2C3D4E8C_C00000000", "target": true },
                { "instanceId": "BTHENUM\\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\\7&B4C5D6E7F809&0&B4C5D6E7F809_C00000000", "target": false },
                { "instanceId": "BTHENUM\\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\\7&c7d8e9f0a1b2&0&c7d8e9f0a1b2_C00000000", "target": false }
              ]
            }
            """);

        IReadOnlyList<SpeakerCandidate> found = SpeakerCandidateFinder.Find(folder.Path);
        CollectionAssert.AreEquivalent(TwoAddresses, found.Select(c => c.Address).ToArray(), "case-insensitive input must come back upper case, and the pinned address must never appear.");
        Assert.IsTrue(found.All(c => c.SourceStamp == "20260921T000000Z" && c.SourceTestId == "01-a2dp-oneshot"),
            "Each candidate must carry the run it actually came from.");
    }

    [TestMethod]
    public void CarriesTheDeviceNameWhenTheEvidenceHoldsOneAndNullWhenItDoesNot()
    {
        using var folder = new Earshot.Tests.TempFolder();
        string testFolder = Path.Combine(folder.Path, "20260921T000000Z", "01-a2dp-oneshot");
        Directory.CreateDirectory(testFolder);

        File.WriteAllText(Path.Combine(testFolder, "nodes-before.json"), """
            {
              "address": "0A1B2C3D4E8C",
              "nodes": [
                { "instanceId": "DEV_B4C5D6E7F809", "name": "Kitchen Speaker" },
                { "instanceId": "DEV_C7D8E9F0A1B2" }
              ]
            }
            """);

        IReadOnlyList<SpeakerCandidate> found = SpeakerCandidateFinder.Find(folder.Path);
        Assert.AreEqual("Kitchen Speaker", found.Single(c => c.Address == "B4C5D6E7F809").DeviceName);
        Assert.IsNull(found.Single(c => c.Address == "C7D8E9F0A1B2").DeviceName);
    }

    [TestMethod]
    public void PrefersTheNewestRunFolderWhenMoreThanOneHasNodeData()
    {
        using var folder = new Earshot.Tests.TempFolder();
        string older = Path.Combine(folder.Path, "20260101T000000Z", "01-a2dp-oneshot");
        string newer = Path.Combine(folder.Path, "20260921T000000Z", "02-disconnect");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);

        File.WriteAllText(Path.Combine(older, "nodes.json"), """{ "address": "0A1B2C3D4E8C", "nodes": [ { "instanceId": "DEV_AAAAAAAAAAAA" } ] }""");
        File.WriteAllText(Path.Combine(newer, "nodes.json"), """{ "address": "0A1B2C3D4E8C", "nodes": [ { "instanceId": "DEV_BBBBBBBBBBBB" } ] }""");

        IReadOnlyList<SpeakerCandidate> found = SpeakerCandidateFinder.Find(folder.Path);
        CollectionAssert.AreEquivalent(NewestAddressOnly, found.Select(c => c.Address).ToArray());
        Assert.AreEqual("20260921T000000Z", found[0].SourceStamp, "Only the newest run's own evidence must ever be offered.");
    }

    [TestMethod]
    public void IgnoresFilesWithNoNodesArrayAndFoldersWithNoUsableFile()
    {
        using var folder = new Earshot.Tests.TempFolder();
        string testFolder = Path.Combine(folder.Path, "20260921T000000Z", "14-set-device-refusal");
        Directory.CreateDirectory(testFolder);

        // result.json's own shape: no "nodes" member at all.
        File.WriteAllText(Path.Combine(testFolder, "result.json"), """{ "test": "14-set-device-refusal", "overall": "pass", "criteria": [] }""");
        File.WriteAllText(Path.Combine(testFolder, "not-json-at-all.json"), "not json");
        File.WriteAllText(Path.Combine(testFolder, "empty-nodes.json"), """{ "address": "0A1B2C3D4E8C", "nodes": [] }""");

        Assert.AreEqual(0, SpeakerCandidateFinder.Find(folder.Path).Count);
    }
}
