using System.Text;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// What EvidenceStore.TryReadResult does with a result.json that is
// missing, empty, truncated, wrong-test, self-contradicting, oddly shaped but valid, or written
// with a byte order mark: rules 1 to 5.
[TestClass]
public sealed class EvidenceStoreReadTests
{
    [TestMethod]
    public void NoFileAtAllIsUnknown()
    {
        using var folder = new TempFolder();
        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "no result.json");
    }

    [TestMethod]
    public void ZeroByteFileIsUnknown()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.File("result.json"), Array.Empty<byte>());
        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "empty");
    }

    [TestMethod]
    public void TruncatedJsonIsUnknown()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("result.json"), "{ \"test\": \"01-a2dp-oneshot\", \"overall\": \"pass\", \"criteria\": [ { \"id\"");
        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "not valid JSON");
    }

    [TestMethod]
    public void WrongTestIdIsUnknown()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("02-disconnect", "pass").WithCriterion("c1", "pass").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "'02-disconnect'");
    }

    [TestMethod]
    public void OverallInCapitalsIsUnknown()
    {
        using var folder = new TempFolder();
        // Built by hand: the fixture only ever writes overall values the reader should accept, so
        // the string is written directly here.
        string json = "{\"test\":\"01-a2dp-oneshot\",\"overall\":\"PASS\",\"criteria\":[{\"id\":\"c1\",\"criterion\":\"x\",\"outcome\":\"pass\",\"detail\":\"\"}]}";
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "PASS");
    }

    [TestMethod]
    public void PassWithAFailedCriterionDisagreesWithItself()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "fail").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "disagrees with itself");
    }

    [TestMethod]
    public void PassWithNoCriteriaDisagreesWithItself()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result);
        StringAssert.Contains(reason, "disagrees with itself");
    }

    [TestMethod]
    public void ARunCriterionMeansTheScriptStoppedEarly()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "fail").WithCriterion("run", "fail", detail: "stopped").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNotNull(result);
        Assert.IsNull(reason);
        Assert.IsTrue(result!.StoppedEarly);
    }

    [TestMethod]
    public void CriteriaAsASingleObjectIsAcceptedAsOneCriterion()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithCriteriaAsSingleObject().Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNotNull(result);
        Assert.IsNull(reason);
        Assert.AreEqual(1, result!.Criteria.Count);
        Assert.AreEqual("c1", result.Criteria[0].Id);
    }

    [TestMethod]
    public void AByteOrderMarkIsAccepted()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json, byteOrderMark: true);

        byte[] bytes = File.ReadAllBytes(folder.File("result.json"));
        Assert.IsTrue(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "The fixture did not actually write a byte order mark.");

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNotNull(result);
        Assert.IsNull(reason);
        Assert.AreEqual("pass", result!.Overall);
    }

    [TestMethod]
    public void FindingsAsASingleObjectIsAcceptedAsOneFinding()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass")
            .WithCriterion("c1", "pass")
            .WithFinding("leftAtRest", "yes")
            .WithFindingsAsSingleObject()
            .Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNotNull(result);
        Assert.IsNull(reason);
        Assert.AreEqual("yes", result!.LeftAtRest);
    }

    [TestMethod]
    public void ANullFindingValueIsNotMeasured()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass")
            .WithCriterion("c1", "pass")
            .WithFinding("leftAtRest", null)
            .Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNotNull(result);
        Assert.IsNull(reason);
        Assert.IsNull(result!.LeftAtRest);
    }

    [TestMethod]
    public void EmptyFolderMeansNoResultJson()
    {
        using var folder = new TempFolder();
        RunEvidence evidence = EvidenceStore.ReadRunEvidence("20260920T000000Z", folder.Path, "01-a2dp-oneshot");
        Assert.IsFalse(evidence.ReadSucceeded);
        StringAssert.Contains(evidence.ReadFailureReason, "no result.json");
    }
}
