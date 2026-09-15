using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Composition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

[TestClass]
public sealed class ProbeTests
{
    private static readonly string[] AllTargets = ["audio", "topology", "nodes", "services", "task", "battery"];
    private static readonly string[] BatteryOnly = ["battery"];

    // Probe battery needs no services; building them here would be a test bug.
    private static ServiceRegistry NoServices() =>
        throw new AssertFailedException("probe battery must not build the service registry.");

    private static Program.ProbeRequest Parse(params string[] args)
    {
        Assert.IsTrue(Program.TryParseProbeArgs(args, out Program.ProbeRequest? request, out string? error), error);
        return request;
    }

    [TestMethod]
    public void NoTargetMeansAll()
    {
        Program.ProbeRequest request = Parse("probe");

        CollectionAssert.AreEqual(AllTargets, request.Targets.ToArray());
        Assert.IsFalse(request.Json);
        Assert.IsNull(request.OutPath);
    }

    [TestMethod]
    public void AllExpandsToEveryTarget()
    {
        CollectionAssert.AreEqual(AllTargets, Parse("probe", "all", "--json").Targets.ToArray());
    }

    [TestMethod]
    public void TargetJsonAndOutInAnyOrder()
    {
        Program.ProbeRequest request = Parse("probe", "--out", "C:\\temp\\p.json", "battery", "--json");

        CollectionAssert.AreEqual(BatteryOnly, request.Targets.ToArray());
        Assert.IsTrue(request.Json);
        Assert.AreEqual("C:\\temp\\p.json", request.OutPath);
    }

    [TestMethod]
    [DataRow("probe", "bogus")]
    [DataRow("probe", "Battery")]
    [DataRow("probe", "battery", "nodes")]
    [DataRow("probe", "--json", "--json")]
    [DataRow("probe", "--out")]
    [DataRow("probe", "--out", "--json")]
    [DataRow("probe", "--out", "a", "--out", "b")]
    [DataRow("probe", "-json")]
    public void RejectsBadArguments(params string[] args)
    {
        Assert.IsFalse(Program.TryParseProbeArgs(args, out Program.ProbeRequest? request, out string? error));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void BatteryTextReportsNoSourceAndNoFigure()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(Parse("probe", "battery"), output, NoServices);

        string text = output.ToString();
        Assert.AreEqual(ExitCodes.Ok, exit);
        Assert.IsTrue(text.Contains("== battery ==", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Battery source: none", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Reading: no value", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("live test 11", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains('%', StringComparison.Ordinal), "No percentage is ever printed.");
    }

    [TestMethod]
    public void BatteryJsonIsOneObjectWithoutAPercent()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(Parse("probe", "battery", "--json"), output, NoServices);

        Assert.AreEqual(ExitCodes.Ok, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        JsonElement root = doc.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual("battery", root.GetProperty("target").GetString());
        Assert.IsFalse(root.GetProperty("hasSource").GetBoolean());
        Assert.IsFalse(root.GetProperty("hasValue").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("percent", out _));
        Assert.IsGreaterThan(0, root.GetProperty("phase0").GetProperty("findings").GetArrayLength());
    }

    [TestMethod]
    public void SeveralTargetsWithJsonFormOneValidArray()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var request = new Program.ProbeRequest(["battery", "battery"], Json: true, OutPath: null);

        int exit = Program.RunProbe(request, output, NoServices);

        Assert.AreEqual(ExitCodes.Ok, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
        Assert.AreEqual("battery", doc.RootElement[1].GetProperty("target").GetString());
    }

    [TestMethod]
    public void OutPathWritesUtf8WithoutByteOrderMark()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "sub", "probe.txt");

        Assert.IsTrue(CommandOutput.TryOpen(path, out CommandOutput? output, out string? problem), problem);
        using (output)
        {
            Assert.AreEqual(path, output.Destination);
            Program.RunProbe(Parse("probe", "battery"), output.Writer, NoServices);
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.IsGreaterThan(3, bytes.Length);
        Assert.IsFalse(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }
}
