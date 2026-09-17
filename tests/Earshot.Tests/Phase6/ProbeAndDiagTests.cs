using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// probe services against the in-memory Bluetooth stack, the protection intent file, and the argument and
// refusal handling of diag protect-unelevated. The live call in diag is never made here.
[TestClass]
public sealed class ProbeAndDiagTests
{
    private static ServiceRegistry NoServices() =>
        throw new AssertFailedException("Writing a probe report must not build the service registry.");

    private static EarshotSettings Pinned() => new()
    {
        PinnedAddress = RecordedNodes.AirPodsAddress,
        PinnedContainerId = RecordedNodes.AirPodsContainer,
    };

    [TestMethod]
    public void ProbeServicesFlagsHandsfreeHeadsetAndA2dpAndChangesNothing()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();

        Program.ServicesProbeReport report = Program.ReadServicesProbe(fake, Pinned(), store);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteServicesProbe(new ProbeContext("services", text, json: false, NoServices), report);

        Assert.IsEmpty(fake.SetCalls);
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path), "The probe writes nothing.");
        Assert.AreEqual("settings", report.UsedFrom);
        Assert.AreEqual(AudioProtectionState.NotProtected, report.Snapshot.State);
        string output = text.ToString();
        Assert.Contains("Handsfree 0000111E: installed", output);
        Assert.Contains("Headset   00001108: not installed", output);
        Assert.Contains("A2DP sink 0000110B: installed", output);
        Assert.Contains("Installed services: 8, complete list", output);
        Assert.Contains("Protection: NotProtected", output);
        Assert.Contains("protection.json: Missing", output);
    }

    [TestMethod]
    public void ProbeServicesJsonIsOneValueWithTheFlags()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        Assert.IsTrue(store.WriteDevice(RecordedNodes.AirPods()).Ok);
        Assert.IsTrue(store.WriteProtection(new ProtectionRecord { DisabledServices = { ProtectedServices.Handsfree } }).Ok);
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake[RecordedNodes.AirPodsAddress].TurnOffOutside(ProtectedServices.Handsfree);

        Program.ServicesProbeReport report = Program.ReadServicesProbe(fake, new EarshotSettings(), store);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteServicesProbe(new ProbeContext("services", text, json: true, NoServices), report);

        using JsonDocument document = JsonDocument.Parse(text.ToString());
        JsonElement root = document.RootElement;
        Assert.AreEqual("services", root.GetProperty("target").GetString());
        Assert.AreEqual("device.json", root.GetProperty("usedFrom").GetString());
        Assert.IsFalse(root.GetProperty("handsfree0000111E").GetBoolean());
        Assert.IsFalse(root.GetProperty("headset00001108").GetBoolean());
        Assert.IsTrue(root.GetProperty("a2dpSink0000110B").GetBoolean());
        Assert.AreEqual("Protected", root.GetProperty("protection").GetString());
        Assert.AreEqual(ProtectedServices.Handsfree.ToString("D"), root.GetProperty("recordedServices")[0].GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("pendingProtect").ValueKind);
        Assert.AreEqual(7, root.GetProperty("installedServices").GetArrayLength());
    }

    [TestMethod]
    public void ProbeServicesWithNothingPinnedReadsNothing()
    {
        using var temp = new TempFolder();
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();

        Program.ServicesProbeReport report = Program.ReadServicesProbe(fake, new EarshotSettings(), new GateStore(temp.Path));

        Assert.IsNull(report.UsedAddress);
        Assert.IsNull(report.Read);
        Assert.IsEmpty(fake.Calls);
        Assert.AreEqual(AudioProtectionState.Unknown, report.Snapshot.State);
    }

    [TestMethod]
    public void AnIncompleteListNeverClaimsAServiceIsAbsent()
    {
        using var temp = new TempFolder();
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake.ServicesResult = 234;
        fake.IncompleteList = [ProtectedServices.AudioSink];

        Program.ServicesProbeReport report = Program.ReadServicesProbe(fake, Pinned(), new GateStore(temp.Path));
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteServicesProbe(new ProbeContext("services", text, json: false, NoServices), report);

        Assert.Contains("Handsfree 0000111E: not in the incomplete list", text.ToString());
        Assert.AreEqual(AudioProtectionState.Unknown, report.Snapshot.State);
    }

    [TestMethod]
    public void TheIntentFileRoundTripsAndIsReadStrictly()
    {
        using var temp = new TempFolder();
        var file = new ProtectionIntentFile(temp.Path);

        Assert.AreEqual(GateReadStatus.Missing, file.Read().Status);
        Assert.IsNull(file.Clear(), "Nothing to clear adds no step.");

        Assert.IsTrue(file.Write(protect: false).Ok);
        GateRead<ProtectionIntent> read = file.Read();
        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.IsFalse(read.Value!.Protect);
        Assert.HasCount(1, Directory.GetFiles(temp.Path), "No temporary file is left behind.");

        StepOutcome? cleared = file.Clear();
        Assert.IsNotNull(cleared);
        Assert.IsTrue(cleared.Ok);
        Assert.IsFalse(File.Exists(file.FilePath));
    }

    [TestMethod]
    [DataRow("{\"SchemaVersion\":1,\"Protect\":true,\"Extra\":1}")]
    [DataRow("{\"SchemaVersion\":2,\"Protect\":true}")]
    [DataRow("{\"SchemaVersion\":1,\"Protect\":\"yes\"}")]
    [DataRow("{\"SchemaVersion\":1}")]
    [DataRow("[true]")]
    [DataRow("not json")]
    public void AnIntentFileOfAnyOtherShapeIsInvalid(string content)
    {
        using var temp = new TempFolder();
        var file = new ProtectionIntentFile(temp.Path);
        File.WriteAllText(file.FilePath, content);

        GateRead<ProtectionIntent> read = file.Read();

        Assert.AreEqual(GateReadStatus.Invalid, read.Status);
        Assert.IsNull(read.Value);
        Assert.IsFalse(read.Step.Ok);
    }

    [TestMethod]
    public void DiagOnTurnsOffHandsfreeThenHeadsetAndNeverTouchesA2dp()
    {
        Assert.IsTrue(Program.TryPlanDiagProtect(["on"], out Program.DiagProtectPlan? plan, out string? error), error);

        Assert.IsTrue(plan.Protect);
        CollectionAssert.AreEqual(
            new[] { new Program.DiagProtectCall(ProtectedServices.Handsfree, false), new Program.DiagProtectCall(ProtectedServices.Headset, false) },
            plan.Calls.ToArray());
    }

    [TestMethod]
    public void DiagOffTurnsHandsfreeBackOnOnly()
    {
        Assert.IsTrue(Program.TryPlanDiagProtect(["off"], out Program.DiagProtectPlan? plan, out string? error), error);

        Assert.IsFalse(plan.Protect);
        CollectionAssert.AreEqual(new[] { new Program.DiagProtectCall(ProtectedServices.Handsfree, true) }, plan.Calls.ToArray());
        Assert.IsFalse(plan.Calls.Any(c => c.Service == ProtectedServices.AudioSink));
    }

    [TestMethod]
    [DataRow("ON")]
    [DataRow("maybe")]
    [DataRow("on", "off")]
    [DataRow("")]
    [DataRow(" on")]
    public void DiagRejectsAnythingButOnOrOff(params string[] args)
    {
        Assert.IsFalse(Program.TryPlanDiagProtect(args, out Program.DiagProtectPlan? plan, out string? error));
        Assert.IsNull(plan);
        Assert.IsFalse(string.IsNullOrEmpty(error));
        Assert.IsFalse(Program.TryPlanDiagProtect([], out _, out _));
    }

    // ERROR_INVALID_PARAMETER from the NULL-radio call may be the radio, not the flags, so only that result makes
    // the live test repeat the call with a radio handle.
    [TestMethod]
    [DataRow(87u, true)]
    [DataRow(0u, false)]
    [DataRow(0x80070057u, false)]
    [DataRow(1060u, false)]
    [DataRow(5u, false)]
    public void DiagRepeatsACallWithARadioHandleOnlyAfterInvalidParameter(uint rc, bool repeat)
    {
        Assert.AreEqual(repeat, Program.RetryWithRadioHandle(rc));
    }

    [TestMethod]
    public void DiagRefusesToRunElevatedOrAsSystem()
    {
        Assert.IsNotNull(Program.DiagProtectRefusal(FakeToken.System));
        Assert.IsNotNull(Program.DiagProtectRefusal(FakeToken.ElevatedUser));
        Assert.IsNull(Program.DiagProtectRefusal(FakeToken.PlainUser));
    }

    [TestMethod]
    public void DiagRefusesWhileAnyTargetNodeIsDisabled()
    {
        FakeNodeApi nodes = RecordedNodes.Table();
        var reader = new NodeStateReader(nodes);
        Assert.IsNull(Program.DiagProtectNodeRefusal(reader.Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)));

        nodes[RecordedNodes.AirPodsTargets[0]].MarkDisabled(persistent: true);
        Assert.IsNotNull(Program.DiagProtectNodeRefusal(reader.Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)));

        FakeNodeApi away = RecordedNodes.Table();
        away[RecordedNodes.AirPodsDeviceNode].Present = false;
        Assert.IsNotNull(Program.DiagProtectNodeRefusal(new NodeStateReader(away).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)));

        FakeNodeApi unlisted = RecordedNodes.Table();
        unlisted.ListResult = 0x13;
        Assert.IsNotNull(Program.DiagProtectNodeRefusal(new NodeStateReader(unlisted).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)));
    }

    [TestMethod]
    public void DiagKeepsTheGatesRuleForAServiceNodeThatIsGone()
    {
        FakeNodeApi nodes = RecordedNodes.Table();
        FakeNode handsfree = nodes[RecordedNodes.AirPodsTargets.Single(id => id.Contains("{0000111E", StringComparison.Ordinal))];
        handsfree.MarkDisabled(persistent: true);
        handsfree.Present = false;

        Assert.IsNull(Program.DiagProtectNodeRefusal(new NodeStateReader(nodes).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress)));
    }

    [TestMethod]
    public void EvidenceThatCannotBeWrittenGoesToTheLogAndTheOutput()
    {
        using var temp = new TempFolder();
        string blocked = Path.Combine(temp.Path, "evidence.json");
        Directory.CreateDirectory(blocked);
        var log = new CapturingLog();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        const string json = "{\"target\":\"protect-unelevated\",\"calls\":[{\"returnValue\":0}]}";

        Assert.IsFalse(Program.WriteDiagEvidence(blocked, json, log, output));

        LogEntry warning = log.Entries.Single(e => e.Level == LogLevel.Warn);
        Assert.Contains(json, warning.Message, "The whole evidence is in the log.");
        Assert.Contains(blocked, warning.Message);
        Assert.Contains(json, output.ToString());
    }

    [TestMethod]
    public void EvidenceIsWrittenWhenItCanBe()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "evidence.json");
        var log = new CapturingLog();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        Assert.IsTrue(Program.WriteDiagEvidence(path, "{}", log, output));

        Assert.AreEqual("{}", File.ReadAllText(path));
        Assert.IsEmpty(log.Entries);
        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public void DiagInSafeModeIsRefusedBeforeThisTargetRuns()
    {
        var request = new Program.DiagRequest("protect-unelevated", ["on"], OutPath: null);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var temp = new TempFolder();

        int exit = Program.RunDiag(request, output, temp.Path, safeMode: true, new CapturingLog(), NoServices);

        Assert.AreEqual(ExitCodes.Refused, exit);
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path));
    }
}
