using System.Text.Json;
using System.Text.Json.Nodes;
using Earshot.Commands;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Commands;

[TestClass]
public sealed class DoctorTests
{
    private static readonly string[] ExpectedHistory =
    [
        "time=1760000000 step=connect outcome=failed code=-2147023673 reason=timeout",
        "time=1760000100 step=connect outcome=failed reason=(text removed)",
        "time=1760000200 step=Connect <address> outcome=true attempt=2",
    ];

    private static string Fenced(string output)
    {
        int start = output.IndexOf("```json", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "No JSON block.");
        start = output.IndexOf('\n', start) + 1;
        int end = output.IndexOf("```", start, StringComparison.Ordinal);
        return output[start..end];
    }

    private static string Run(IDoctorSource source)
    {
        var writer = new StringWriter();
        Assert.AreEqual(0, DoctorCommand.Run(source, writer));
        return writer.ToString();
    }

    [TestMethod]
    public void NoPlantedValueSurvivesIntoTheSummaryOrTheJson()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));

        foreach (string planted in DoctorFixture.Planted)
        {
            Assert.IsFalse(output.Contains(planted, StringComparison.OrdinalIgnoreCase), "\"" + planted + "\" survived:\n" + output);
        }
    }

    [TestMethod]
    public void TheReportNamesOnlyAirPodsModelsAndCountsEveryOtherAppleNode()
    {
        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(DoctorFixture.Facts()))));
        JsonElement root = report.RootElement;

        JsonElement pods = root.GetProperty("airpods");
        Assert.AreEqual(2, pods.GetArrayLength(), "The Pro 3 node and the Pro node; the iPhone is not an AirPods model and the other node is not Apple's.");
        Assert.AreEqual("AirPods Pro 3", pods[0].GetProperty("model").GetString());
        Assert.AreEqual(43, pods[0].GetProperty("problemCode").GetInt32());
        Assert.IsTrue(pods[0].GetProperty("disabled").GetBoolean());
        Assert.AreEqual("AirPods Pro", pods[1].GetProperty("model").GetString());
        Assert.AreEqual(JsonValueKind.Null, pods[1].GetProperty("disabled").ValueKind);
        Assert.AreEqual(1, root.GetProperty("otherAppleNodes").GetInt32(), "The iPhone's node is counted and not described.");
    }

    [TestMethod]
    public void WindowsOwnEnumeratorIsNotReportedAsAFilterFromAnotherProgram()
    {
        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(DoctorFixture.Facts()))));

        JsonElement filters = report.RootElement.GetProperty("filterDrivers");
        Assert.AreEqual(1, filters.GetArrayLength());
        Assert.AreEqual("BattMonFltr", filters[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public void TheConnectHistoryKeepsPlainTokensAndDropsAnythingElse()
    {
        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(DoctorFixture.Facts()))));

        JsonElement history = report.RootElement.GetProperty("connectHistory");
        Assert.AreEqual(@"%LOCALAPPDATA%\Earshot\logs\connect-history.jsonl", history.GetProperty("path").GetString());
        string[] last = history.GetProperty("last").EnumerateArray().Select(e => e.GetString()!).ToArray();
        CollectionAssert.AreEqual(ExpectedHistory, last);
    }

    [TestMethod]
    public void AReadThatFailedIsReportedAsUnreadableAndTheRestStillComes()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        JsonElement unreadable = report.RootElement.GetProperty("unreadable");
        Assert.AreEqual(1, unreadable.GetArrayLength());
        Assert.AreEqual("tray", unreadable[0].GetProperty("part").GetString());
        StringAssert.Contains(output, "Could not read: tray.");
        Assert.IsFalse(report.RootElement.GetProperty("tray").GetProperty("running").GetBoolean());
    }

    [TestMethod]
    public void AnEmptyPcStillMakesAReportWithEveryKey()
    {
        string output = Run(new FakeDoctorSource(new DoctorFacts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        Assert.AreEqual(1, report.RootElement.GetProperty("schema").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("earshot").GetProperty("installed").GetBoolean());
        StringAssert.Contains(output, "Earshot: not installed.");
        StringAssert.Contains(output, "AirPods in Windows: none found.");
    }

    // The one real run: this PC, read and not changed, through the real source. It asserts what must hold on any PC.
    [TestMethod]
    public void TheRealSourceReadsThisPcAndTheReportCarriesNeitherTheUserNorThePcName()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name switch
        {
            Paths.DataRootVariable => temp.File("data"),
            Paths.SafeModeVariable => "1",
            _ => null,
        });
        var source = new RealDoctorSource(paths, new CapturingLog());

        string output = Run(source);

        using JsonDocument report = JsonDocument.Parse(Fenced(output));
        Assert.AreEqual(1, report.RootElement.GetProperty("schema").GetInt32());
        Assert.AreEqual(3, report.RootElement.GetProperty("tasks").GetArrayLength());
        Assert.AreEqual(3, report.RootElement.GetProperty("bluetoothServices").GetArrayLength());
        Assert.IsFalse(output.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "The PC name survived.");
        if (Environment.UserName.Length > 2)
        {
            Assert.IsFalse(output.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "The user name survived.");
        }
    }

    private static HashSet<string> Keys(JsonNode? node, string path = "")
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> p in o)
                {
                    string child = path.Length == 0 ? p.Key : path + "." + p.Key;
                    keys.Add(child);
                    keys.UnionWith(Keys(p.Value, child));
                }

                break;
            case JsonArray a:
                foreach (JsonNode? item in a)
                {
                    keys.UnionWith(Keys(item, path + "[]"));
                }

                break;
        }

        return keys;
    }

    // The same facts through the C# report. The install script's Doctor action is held to this by a test of its own, which
    // reads the same JSON and compares the whole report.
    internal static JsonNode ReportFor(DoctorFacts facts) => JsonNode.Parse(DoctorReport.Build(facts))!;

    [TestMethod]
    public void TheKeysOfTheReportAreTheOnesTheScriptHasToo()
    {
        HashSet<string> keys = Keys(ReportFor(DoctorFixture.Facts()));

        string[] expected =
        [
            "schema", "earshot", "earshot.installed", "earshot.folder", "earshot.version", "earshot.installedAppsEntry", "earshot.installedAppsVersion", "earshot.startMenuShortcut", "earshot.runValue",
            "tray", "tray.running", "tray.processes", "tasks", "tasks[].name", "tasks[].present", "tasks[].state",
            "handBackService", "handBackService.present", "handBackService.state",
            "airpods", "airpods[].model", "airpods[].bus", "airpods[].present", "airpods[].status", "airpods[].problemCode", "airpods[].disabled",
            "otherAppleNodes", "bluetoothServices", "bluetoothServices[].name", "bluetoothServices[].state",
            "radio", "radio.found", "radio.driverVersion", "filterDrivers", "filterDrivers[].name", "filterDrivers[].on",
            "connectHistory", "connectHistory.path", "connectHistory.exists", "connectHistory.lines", "connectHistory.last",
            "battery", "battery.source", "battery.lastReadingSaved", "unreadable", "unreadable[].part", "unreadable[].reason",
        ];
        CollectionAssert.AreEquivalent(expected, keys.ToArray());
    }
}
