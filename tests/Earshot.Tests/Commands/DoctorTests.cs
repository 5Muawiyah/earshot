using System.Text.Json;
using System.Text.Json.Nodes;
using Earshot.Bluetooth;
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
        // A code, a try count and a duration outside their ranges are dropped, as is a number under a word key or in a time that is not one.
        "time=1760000300 step=connect outcome=failed",
        "outcome=seen <address> and <address> reason=ok",
        "time=2026-10-12T09:15:30.123Z step=connect outcome=ok code=0 attempt=1 durationMs=850",
        // Text under a number key (a time that is not a plain UTC time, a code written as text) is dropped.
        "step=connect outcome=ok",
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
        Assert.AreEqual("model 2027", pods[0].GetProperty("model").GetString(), "No published source names 2027, so the report gives the code and not a product name.");
        Assert.AreEqual(43, pods[0].GetProperty("problemCode").GetInt32());
        Assert.IsTrue(pods[0].GetProperty("disabled").GetBoolean());
        Assert.AreEqual("AirPods Pro", pods[1].GetProperty("model").GetString());
        Assert.AreEqual(JsonValueKind.Null, pods[1].GetProperty("disabled").ValueKind);
        Assert.AreEqual(1, root.GetProperty("otherAppleNodes").GetInt32(), "The iPhone's node is counted and not described.");
    }

    [TestMethod]
    public void TheSummaryCountsOtherAppleEntriesAndSaysWhatTheyMayBe()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));

        StringAssert.Contains(output, "1 other Apple entry: an iPhone or AirPods of a model not listed.");
        Assert.IsFalse(Run(new FakeDoctorSource(new DoctorFacts())).Contains("other Apple", StringComparison.Ordinal), "No line when there are none.");
    }

    [TestMethod]
    public void AnUnlistedModelOfAirPodsIsNotReportedAsNoneFound()
    {
        DoctorFacts facts = DoctorFixture.Facts();
        facts.Devices = facts.Devices!.Where(d => d.InstanceId.Contains("_PID&12A8", StringComparison.Ordinal)).ToList();

        string output = Run(new FakeDoctorSource(facts));

        StringAssert.Contains(output, "AirPods in Windows: none found.");
        StringAssert.Contains(output, "1 other Apple entry: an iPhone or AirPods of a model not listed.");
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
    public void PairedAirPodsThatAreNotTheProtectedOnesAreCountedAndSaidInTheSummary()
    {
        DoctorFacts facts = DoctorFixture.Facts();
        facts.Protection = new ProtectionFact { Pinned = true, Unprotected = 2 };

        string output = Run(new FakeDoctorSource(facts));

        using JsonDocument report = JsonDocument.Parse(Fenced(output));
        JsonElement protection = report.RootElement.GetProperty("protection");
        Assert.IsTrue(protection.GetProperty("pinned").GetBoolean());
        Assert.AreEqual(2, protection.GetProperty("unprotectedAirPods").GetInt32());
        Assert.Contains("not the ones Earshot protects (2)", output, StringComparison.Ordinal);
    }

    [TestMethod]
    public void NothingUnprotectedOrNoPinOrAnUnreadListSaysNothingInTheSummary()
    {
        foreach (ProtectionFact? fact in new ProtectionFact?[] { new() { Pinned = true, Unprotected = 0 }, new() { Pinned = false, Unprotected = 3 }, null })
        {
            DoctorFacts facts = DoctorFixture.Facts();
            facts.Protection = fact;

            string output = Run(new FakeDoctorSource(facts));

            Assert.DoesNotContain("not the ones Earshot protects", output, StringComparison.Ordinal);
        }
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

    // The same facts through the C# report. The install script's Doctor action makes no report of its own: it runs the program's
    // doctor command and shows what it printed, so this report is the one a person or an AI reads either way.
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
            "radio", "radio.found", "radio.adapterFound", "radio.classicSupported", "radio.state", "radio.radioState", "radio.devNodeStatus", "radio.problemCode",
            "radio.driverVersion", "radio.driverDate", "radio.readCodes",
            "startup", "startup.runValue", "startup.target", "startup.targetExists", "startup.approvedFlag", "startup.turnedOffInWindows",
            "pairing", "pairing[].time", "pairing[].outcome", "pairing[].status", "pairing[].statusValue", "pairing[].modelCheck", "pairing[].durationMs",
            "filterDrivers", "filterDrivers[].name", "filterDrivers[].on",
            "connectHistory", "connectHistory.path", "connectHistory.exists", "connectHistory.lines", "connectHistory.last",
            "protection", "protection.pinned", "protection.unprotectedAirPods",
            "battery", "battery.source", "battery.lastReadingSaved", "unreadable", "unreadable[].part", "unreadable[].reason",
        ];
        CollectionAssert.AreEquivalent(expected, keys.ToArray());
    }

    [TestMethod]
    public void TheRadioIsReportedWithItsStateProblemCodeDriverAndReadCodes()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        JsonElement radio = report.RootElement.GetProperty("radio");
        Assert.IsTrue(radio.GetProperty("adapterFound").GetBoolean());
        Assert.IsTrue(radio.GetProperty("classicSupported").GetBoolean());
        Assert.AreEqual("Problem", radio.GetProperty("state").GetString());
        Assert.AreEqual("On", radio.GetProperty("radioState").GetString());
        Assert.AreEqual("0x00000400", radio.GetProperty("devNodeStatus").GetString());
        Assert.AreEqual(10, radio.GetProperty("problemCode").GetInt32());
        Assert.AreEqual("23.60.0.2", radio.GetProperty("driverVersion").GetString());
        Assert.AreEqual("2024-03-09", radio.GetProperty("driverDate").GetString());
        Assert.AreEqual(2, radio.GetProperty("readCodes").GetArrayLength());
        StringAssert.Contains(output, "Bluetooth radio: Problem, problem code 10, driver 23.60.0.2 (2024-03-09).");
    }

    // Nothing was read, so nothing is said to be missing. Before, this said "none found", which the AI guide reads as no adapter.
    [TestMethod]
    public void ARadioNobodyReadSaysNotReadAndEveryRadioKeyIsStillThere()
    {
        string output = Run(new FakeDoctorSource(new DoctorFacts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        JsonElement radio = report.RootElement.GetProperty("radio");
        Assert.IsFalse(radio.GetProperty("found").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, radio.GetProperty("state").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, radio.GetProperty("problemCode").ValueKind);
        Assert.AreEqual(0, radio.GetProperty("readCodes").GetArrayLength());
        StringAssert.Contains(output, "Bluetooth radio: not read.");
        Assert.IsFalse(output.Contains("Bluetooth radio: none found", StringComparison.Ordinal));
    }

    // Only a read that answered and found no adapter says none was found; a read that did not finish says it was not read.
    [TestMethod]
    public void OnlyAnAnsweredReadWithNoAdapterSaysNoneFound()
    {
        string none = Run(new FakeDoctorSource(new DoctorFacts { Radio = new RadioFact { AdapterFound = false, State = "NoAdapter" } }));
        string unread = Run(new FakeDoctorSource(new DoctorFacts { Radio = new RadioFact { State = "Unknown" } }));
        string notAnswered = Run(new FakeDoctorSource(new DoctorFacts { Radio = new RadioFact { AdapterFound = false, State = "Unknown" } }));

        StringAssert.Contains(none, "Bluetooth radio: none found.");
        StringAssert.Contains(unread, "Bluetooth radio: not read (Unknown).");
        StringAssert.Contains(notAnswered, "Bluetooth radio: not read (Unknown).");
        Assert.IsFalse(unread.Contains("Bluetooth radio: none found", StringComparison.Ordinal));
    }

    // An adapter with no Classic Bluetooth was found: the check stops before its node is read, and doctor still says it is
    // there. Before, the unread node made it "none found (NoClassic)", the opposite of the card's line.
    [TestMethod]
    public void AnAdapterWithoutClassicBluetoothIsFound()
    {
        var result = new RadioCheckResult(RadioCheckState.NoClassic, RadioLines.For(RadioCheckState.NoClassic, null), RadioLines.ActionFor(RadioCheckState.NoClassic),
            AdapterFound: true, ClassicSupported: false, RadioState: null, DevNodeStatus: null, ProblemCode: null, DriverVersion: null, DriverDate: null, ReadCodes: [], ElapsedMs: 5);

        RadioFact fact = RealDoctorSource.RadioFactFrom(result);
        string output = Run(new FakeDoctorSource(new DoctorFacts { Radio = fact }));

        Assert.IsTrue(fact.Found);
        StringAssert.Contains(output, "Bluetooth radio: NoClassic");
        Assert.IsFalse(output.Contains("Bluetooth radio: none found", StringComparison.Ordinal));
    }

    // Doctor runs in a process of its own, cold, so its read of the radio is given longer than the card's.
    [TestMethod]
    public void DoctorGivesTheRadioReadLongerThanTheCard()
    {
        Assert.IsGreaterThan(RadioCheck.DefaultBudget, RealDoctorSource.RadioBudget);
    }

    [TestMethod]
    public void ARadioFieldThatIsNotAPlainValueIsDropped()
    {
        DoctorFacts facts = DoctorFixture.Facts();
        facts.Radio!.State = "Jonathan's PC is off";
        facts.Radio.DevNodeStatus = "0A1B2C3D4E8C";
        facts.Radio.DriverDate = "last Tuesday";

        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(facts))));

        JsonElement radio = report.RootElement.GetProperty("radio");
        Assert.AreEqual(JsonValueKind.Null, radio.GetProperty("state").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, radio.GetProperty("devNodeStatus").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, radio.GetProperty("driverDate").ValueKind);
    }

    [TestMethod]
    public void TheLastPairingRunsAreReportedAsPlainTokensAndTheSummaryNamesTheLast()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        JsonElement pairing = report.RootElement.GetProperty("pairing");
        Assert.AreEqual(3, pairing.GetArrayLength());
        Assert.AreEqual("AuthenticationFailure", pairing[0].GetProperty("status").GetString());
        Assert.AreEqual(9, pairing[0].GetProperty("statusValue").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, pairing[0].GetProperty("modelCheck").ValueKind);
        Assert.AreEqual("airpods", pairing[1].GetProperty("modelCheck").GetString());
        Assert.AreEqual(21000, pairing[1].GetProperty("durationMs").GetInt64());
        // Free text, an address-sized number and a negative duration from a writer are dropped, as in the history.
        foreach (string key in new[] { "time", "outcome", "status", "statusValue", "modelCheck", "durationMs" })
        {
            Assert.AreEqual(JsonValueKind.Null, pairing[2].GetProperty(key).ValueKind, key);
        }
    }

    [TestMethod]
    public void OnlyTheLastFivePairingRunsAreReported()
    {
        DoctorFacts facts = DoctorFixture.Facts();
        facts.History!.Pairing = Enumerable.Range(1, 8).Select(i => new PairingFact { Outcome = "failed", Status = "Failed", StatusValue = 19, Time = "2026-10-25T20:0" + i + ":00Z" }).ToList();

        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(facts))));

        JsonElement pairing = report.RootElement.GetProperty("pairing");
        Assert.AreEqual(5, pairing.GetArrayLength());
        Assert.AreEqual("2026-10-25T20:08:00Z", pairing[4].GetProperty("time").GetString(), "Newest last.");
    }

    [TestMethod]
    public void TheSummarySaysTheLastPairingAndNothingWhenThereWasNone()
    {
        StringAssert.Contains(Run(new FakeDoctorSource(DoctorFixture.Facts())), "Last pairing: unknown (no status).");
        DoctorFacts facts = DoctorFixture.Facts();
        facts.History!.Pairing = [new PairingFact { Time = "2026-10-25T20:08:22Z", Outcome = "paired", Status = "Paired", StatusValue = 0 }];

        StringAssert.Contains(Run(new FakeDoctorSource(facts)), "Last pairing: paired (Paired) at 2026-10-25T20:08:22Z.");
        Assert.DoesNotContain("Last pairing", Run(new FakeDoctorSource(new DoctorFacts())), StringComparison.Ordinal);
    }

    // A Run value on another drive keeps only its file name: the folders around it are the person's own.
    [TestMethod]
    public void ARunValueTargetOutsideTheProfileAndProgramFilesKeepsOnlyItsFileName()
    {
        DoctorFacts facts = DoctorFixture.Facts();
        facts.Startup!.RunCommand = "\"D:\\Tools\\Mine\\Earshot.exe\" --startup";
        using JsonDocument report = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(facts))));

        Assert.AreEqual(@"(another folder)\Earshot.exe", report.RootElement.GetProperty("startup").GetProperty("target").GetString());
    }

    [TestMethod]
    public void TheRunValueTargetAndItsPresenceAreReported()
    {
        string output = Run(new FakeDoctorSource(DoctorFixture.Facts()));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));

        JsonElement startup = report.RootElement.GetProperty("startup");
        Assert.IsTrue(startup.GetProperty("runValue").GetBoolean());
        Assert.AreEqual(@"%LOCALAPPDATA%\Programs\Earshot\Earshot.exe", startup.GetProperty("target").GetString(), "The user's folder is written as a variable.");
        Assert.AreEqual(JsonValueKind.False, startup.GetProperty("targetExists").ValueKind);
        Assert.AreEqual("02", startup.GetProperty("approvedFlag").GetString());
        Assert.AreEqual(JsonValueKind.False, startup.GetProperty("turnedOffInWindows").ValueKind);
        StringAssert.Contains(output, "Open on startup: the Run value points at a file that is not there.");
    }

    [TestMethod]
    public void AFlagWithBitOneSetIsReportedAsTurnedOffInWindowsAndNoRunValueSaysNothing()
    {
        DoctorFacts off = DoctorFixture.Facts();
        off.Startup!.ApprovedFlag = "03";
        off.Startup.TargetExists = true;
        string output = Run(new FakeDoctorSource(off));
        using JsonDocument report = JsonDocument.Parse(Fenced(output));
        Assert.AreEqual(JsonValueKind.True, report.RootElement.GetProperty("startup").GetProperty("turnedOffInWindows").ValueKind);
        StringAssert.Contains(output, "Open on startup: turned off in Windows' startup settings.");
        Assert.DoesNotContain("points at a file that is not there", output, StringComparison.Ordinal);

        using JsonDocument none = JsonDocument.Parse(Fenced(Run(new FakeDoctorSource(new DoctorFacts()))));
        JsonElement startup = none.RootElement.GetProperty("startup");
        Assert.IsFalse(startup.GetProperty("runValue").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, startup.GetProperty("target").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, startup.GetProperty("approvedFlag").ValueKind);
    }

    [TestMethod]
    public void TheTargetOfARunCommandIsTheQuotedPathOrTheTextBeforeTheFirstOption()
    {
        Assert.AreEqual(@"C:\Program Files\Earshot\Earshot.exe", StartupFact.TargetOf("\"C:\\Program Files\\Earshot\\Earshot.exe\" --startup"));
        Assert.AreEqual(@"C:\Earshot\Earshot.exe", StartupFact.TargetOf(@"C:\Earshot\Earshot.exe --startup"));
        Assert.AreEqual(@"C:\Earshot\Earshot.exe", StartupFact.TargetOf(@"C:\Earshot\Earshot.exe"));
        Assert.IsNull(StartupFact.TargetOf(null));
        Assert.IsNull(StartupFact.TargetOf("  "));
    }

    [TestMethod]
    public void APairEntryOfTheConnectHistoryBecomesAPairingFactWithItsStatusAndModelCheck()
    {
        var entry = new Earshot.Diagnostics.ConnectHistoryEntry(
            new DateTimeOffset(2026, 10, 25, 20, 8, 22, TimeSpan.Zero), "pair", "none", "custom-pairing",
            [new Earshot.Diagnostics.HistoryStep("pair", 0, "Paired"), new Earshot.Diagnostics.HistoryStep("model-check", 1, "not-airpods")],
            false, "not-airpods", "None", 21000);

        PairingFact fact = RealDoctorSource.PairingOf(entry);

        Assert.AreEqual("2026-10-25T20:08:22Z", fact.Time);
        Assert.AreEqual("not-airpods", fact.Outcome);
        Assert.AreEqual("Paired", fact.Status);
        Assert.AreEqual(0, fact.StatusValue);
        Assert.AreEqual("not-airpods", fact.ModelCheck);
        Assert.AreEqual(21000, fact.DurationMs);
    }

    // The one real run of the new reads: this PC's radio check and Run value, read only, through the real source.
    [TestMethod]
    public void TheRealSourceReadsTheRadioWithTheCheckAndTheStartupValueWithoutChangingThem()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name switch
        {
            Paths.DataRootVariable => temp.File("data"),
            Paths.SafeModeVariable => "1",
            _ => null,
        });

        DoctorFacts facts = new RealDoctorSource(paths, new CapturingLog()).Read();

        Assert.IsNotNull(facts.Radio, "The radio part is always filled in; its failures are in ReadCodes.");
        Assert.IsNotNull(facts.Radio.State);
        Assert.IsNotNull(facts.Radio.ReadCodes);
        Assert.IsNotNull(facts.Startup);
        Assert.IsNotNull(facts.History!.Pairing);
        using JsonDocument report = JsonDocument.Parse(DoctorReport.Build(facts));
        Assert.AreEqual(facts.Radio.State, report.RootElement.GetProperty("radio").GetProperty("state").GetString());
    }
}
