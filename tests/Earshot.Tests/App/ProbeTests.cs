using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

[TestClass]
public sealed class ProbeTests
{
    private static readonly string[] AllTargets = ["audio", "topology", "nodes", "services", "task", "battery"];
    private static readonly string[] BatteryOnly = ["battery"];
    private const string IconFolder = @"C:\temp\icons";
    private static readonly string[] IconOnly = ["icon"];
    private static readonly int[] IconSizes = [16, 24, 32];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The icon probe needs no services; building them here would be a test bug.
    private static ServiceRegistry NoServices() =>
        throw new AssertFailedException("this probe must not build the service registry.");

    // The battery probe reads the pinned device from the settings and Windows' figure from the registry's battery
    // provider: both are fakes here, so no test reads this machine.
    private static Func<ServiceRegistry> BatteryServices(IBatteryProvider provider, bool pinned = true)
    {
        var temp = new TempFolder();
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        if (pinned)
        {
            settings.Update(s =>
            {
                s.PinnedContainerId = Phase4.RecordedNodes.AirPodsContainer;
                s.PinnedAddress = Phase4.RecordedNodes.AirPodsAddress;
            });
        }

        return () => new ServiceRegistry(log, settings, action => action(), safeMode: false) { Battery = provider };
    }

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
    public void BatteryTextSaysWhatWindowsReadsAndLeavesTheBroadcastToTheTray()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var provider = new FakeBatteryProvider(percent: 70);

        int exit = Program.RunProbe(Parse("probe", "battery"), output, BatteryServices(provider));

        string text = output.ToString();
        Assert.AreEqual(ExitCodes.Ok, exit);
        Assert.IsTrue(text.Contains("== battery ==", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Windows' Hands-Free battery: 70% (from a device node)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains(Program.BatteryBroadcastNote, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains(Program.BatteryObjectsNote, StringComparison.Ordinal));
        Assert.AreEqual(Phase4.RecordedNodes.AirPodsContainer, provider.LastContainer, "It asks about the pinned device.");
        Assert.AreEqual(Phase4.RecordedNodes.AirPodsAddress, provider.LastAddress);
    }

    [TestMethod]
    public void BatteryTextWithNoFigureSaysWhyAndNamesEveryFailedStepWithItsRawCode()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var provider = new FakeBatteryProvider(
            note: "No figure: the property was empty or absent on 8 device nodes.",
            steps: [StepOutcomes.FromConfigRet("hands-free-battery:cm-property", Earshot.Interop.CfgMgr32.CR_FAILURE, "A property could not be read.")]);

        int exit = Program.RunProbe(Parse("probe", "battery"), output, BatteryServices(provider));

        string text = output.ToString();
        Assert.AreEqual(ExitCodes.Ok, exit);
        Assert.IsTrue(text.Contains("Windows' Hands-Free battery: no figure. No figure: the property was empty or absent on 8 device nodes.", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("step failed: hands-free-battery:cm-property CR_FAILURE", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains('%', StringComparison.Ordinal), "No figure, so no percentage is printed.");
    }

    [TestMethod]
    public void BatteryJsonIsOneObjectWithTheFigureItsOriginAndTheSteps()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(Parse("probe", "battery", "--json"), output, BatteryServices(new FakeBatteryProvider(percent: 70)));

        Assert.AreEqual(ExitCodes.Ok, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        JsonElement root = doc.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
        Assert.AreEqual("battery", root.GetProperty("target").GetString());
        Assert.IsTrue(root.GetProperty("hasSource").GetBoolean());
        Assert.IsTrue(root.GetProperty("hasValue").GetBoolean());
        Assert.AreEqual(70, root.GetProperty("percent").GetInt32());
        Assert.AreEqual("device node", root.GetProperty("origin").GetString());
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("steps").ValueKind);
        Assert.AreEqual(Program.BatteryBroadcastNote, root.GetProperty("broadcast").GetString());
    }

    [TestMethod]
    public void BatteryJsonWithNoFigureHasNoPercent()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        Program.RunProbe(Parse("probe", "battery", "--json"), output, BatteryServices(new FakeBatteryProvider()));

        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(doc.RootElement.GetProperty("hasValue").GetBoolean());
        Assert.IsFalse(doc.RootElement.TryGetProperty("percent", out _));
        Assert.AreEqual("No figure.", doc.RootElement.GetProperty("note").GetString());
    }

    [TestMethod]
    public void NothingPinnedIsAConfigurationAnswerNotAFailure()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(Parse("probe", "battery", "--json"), output, BatteryServices(new FakeBatteryProvider(), pinned: false));

        Assert.AreEqual(ExitCodes.Config, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.IsTrue(doc.RootElement.GetProperty("hasSource").GetBoolean());
        Assert.IsFalse(doc.RootElement.GetProperty("hasValue").GetBoolean());
    }

    [TestMethod]
    public void TheNullProviderHasNoSourceAndSaysSo()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(Parse("probe", "battery"), output, BatteryServices(new Earshot.Contracts.Null.NoBatterySource()));

        Assert.AreEqual(ExitCodes.Config, exit);
        Assert.IsTrue(output.ToString().Contains("no source in this build", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SeveralTargetsWithJsonFormOneValidArray()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var request = new Program.ProbeRequest(["battery", "battery"], Json: true, OutPath: null);

        int exit = Program.RunProbe(request, output, BatteryServices(new FakeBatteryProvider(percent: 70)));

        Assert.AreEqual(ExitCodes.Ok, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
        Assert.AreEqual("battery", doc.RootElement[1].GetProperty("target").GetString());
    }

    [TestMethod]
    public void IconIsNamedOnlyAndNeedsAFolder()
    {
        Program.ProbeRequest request = Parse("probe", "icon", "--out", IconFolder);

        CollectionAssert.AreEqual(IconOnly, request.Targets.ToArray());
        Assert.IsTrue(Program.IsIconProbe(request));
        Assert.AreEqual(IconFolder, request.OutPath);
        Assert.IsFalse(Program.IsIconProbe(Parse("probe", "battery", "--out", "x.txt")));
        CollectionAssert.DoesNotContain(Parse("probe", "all").Targets.ToArray(), Program.ProbeIconTarget, "icon writes files, so all never runs it.");

        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "icon"], out _, out string? error));
        Assert.AreEqual("probe icon needs --out <folder>.", error);
        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "icon", "battery", "--out", "x"], out _, out _));
    }

    [TestMethod]
    public void TheIconProbeWritesEveryStateAtEveryDpiInBothInks()
    {
        using var temp = new TempFolder();
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(new Program.ProbeRequest(IconOnly, Json: true, OutPath: temp.Path), output, NoServices);

        Assert.AreEqual(ExitCodes.Ok, exit, output.ToString());
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        JsonElement files = doc.RootElement.GetProperty("files");
        Assert.AreEqual(3 * 4 * 2, files.GetArrayLength());
        foreach (JsonElement file in files.EnumerateArray())
        {
            Assert.IsTrue(file.GetProperty("icoLoads").GetBoolean(), file.GetProperty("file").GetString());
            string path = file.GetProperty("file").GetString()!;
            byte[] png = File.ReadAllBytes(path);
            int px = file.GetProperty("pixels").GetInt32();

            // PNG signature, then the IHDR width and height as big-endian integers.
            CollectionAssert.AreEqual(PngSignature, png.Take(8).ToArray(), path);
            Assert.AreEqual(px, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], path);
            Assert.AreEqual(px, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23], path);
        }

        Assert.HasCount(24, Directory.GetFiles(temp.Path, "earbud-*.png"));
    }

    [TestMethod]
    public void TheIconSizesFollowTheMetricOrTheScaledSmallIcon()
    {
        using var temp = new TempFolder();

        IReadOnlyList<Program.ProbeIconFile> measured = Program.RenderProbeIcons(temp.Path, dpi => dpi == 96 ? 16 : dpi == 144 ? 24 : 32);
        IReadOnlyList<Program.ProbeIconFile> fallback = Program.RenderProbeIcons(Path.Combine(temp.Path, "fallback"), _ => 0);

        CollectionAssert.AreEqual(IconSizes, measured.Select(f => f.Pixels).Distinct().ToArray());
        Assert.IsEmpty(fallback.Where(f => f.Problem is null), "The fallback folder does not exist, so every write reports its problem.");
        CollectionAssert.AreEqual(IconSizes, fallback.Select(f => f.Pixels).Distinct().ToArray(), "16 px scaled by dpi / 96.");
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
            Program.RunProbe(Parse("probe", "battery"), output.Writer, BatteryServices(new FakeBatteryProvider(percent: 70)));
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.IsGreaterThan(3, bytes.Length);
        Assert.IsFalse(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    // ---- probe service ----

    private static readonly string[] ServiceOnly = ["service"];

    [TestMethod]
    public void ProbeServiceIsANamedTargetAndNotPartOfAll()
    {
        CollectionAssert.AreEqual(ServiceOnly, Parse("probe", "service", "--json").Targets.ToArray());
        CollectionAssert.AreEqual(AllTargets, Parse("probe", "all").Targets.ToArray());
    }

    private static (int Exit, string Output) RunServiceProbe(Earshot.Tests.Service.FakeServiceControl service, bool json)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var ctx = new ProbeContext("service", writer, json, NoServices);

        int exit = Program.WriteServiceProbe(ctx, service, @"C:\Program Files\Earshot");

        return (exit, writer.ToString());
    }

    [TestMethod]
    public void ProbeServiceReportsARegisteredServiceAsJsonAndChangesNothing()
    {
        var service = new Earshot.Tests.Service.FakeServiceControl();
        service.Install(Earshot.Interop.AdvApi32.SERVICE_RUNNING, Earshot.Service.ServicePlan.Spec(@"C:\Program Files\Earshot"));

        (int exit, string output) = RunServiceProbe(service, json: true);

        Assert.AreEqual(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.AreEqual("service", root.GetProperty("target").GetString());
        Assert.AreEqual("EarshotHandBack", root.GetProperty("name").GetString());
        Assert.IsTrue(root.GetProperty("present").GetBoolean());
        Assert.AreEqual("running", root.GetProperty("summary").GetString());
        Assert.AreEqual("running", root.GetProperty("state").GetString());
        Assert.AreEqual(4, root.GetProperty("stateCode").GetInt32());
        Assert.AreEqual(2, root.GetProperty("startType").GetInt32());
        Assert.AreEqual(10_000, root.GetProperty("preshutdownMs").GetInt32());
        Assert.AreEqual("LocalSystem", root.GetProperty("account").GetString());
        Assert.AreEqual(Earshot.Service.ServicePlan.Sddl, root.GetProperty("sddl").GetString());
        Assert.AreEqual(0, root.GetProperty("problems").GetArrayLength());
        Assert.AreEqual("\"C:\\Program Files\\Earshot\\Earshot.exe\" service", root.GetProperty("imagePath").GetString());
        Assert.AreEqual("query", string.Join(",", service.Calls), "Read-only: one query and no change.");
    }

    [TestMethod]
    public void ProbeServiceReportsAServiceThatDiffersWithEachDifference()
    {
        var service = new Earshot.Tests.Service.FakeServiceControl();
        service.Install(Earshot.Interop.AdvApi32.SERVICE_STOPPED, Earshot.Service.ServicePlan.Spec(@"C:\Program Files\Earshot"));
        service.ReadStartType = Earshot.Interop.AdvApi32.SERVICE_DEMAND_START;

        (int exit, string output) = RunServiceProbe(service, json: true);

        Assert.AreEqual(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.AreEqual("differs: The start type is 3, not 2.", document.RootElement.GetProperty("summary").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("problems").GetArrayLength());
    }

    [TestMethod]
    public void ProbeServiceReportsAMissingServiceAsAnAnswer()
    {
        var service = new Earshot.Tests.Service.FakeServiceControl();

        (int exit, string output) = RunServiceProbe(service, json: true);

        Assert.AreEqual(0, exit, "A service that is not registered is a normal answer.");
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.IsFalse(root.GetProperty("present").GetBoolean());
        Assert.IsTrue(root.GetProperty("readable").GetBoolean());
        Assert.AreEqual("missing", root.GetProperty("summary").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("stateCode").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("sddl").ValueKind);
        Assert.AreEqual(1060, root.GetProperty("steps")[0].GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void ProbeServiceTextNamesTheStateAndTheImagePath()
    {
        var service = new Earshot.Tests.Service.FakeServiceControl();
        service.Install(Earshot.Interop.AdvApi32.SERVICE_RUNNING, Earshot.Service.ServicePlan.Spec(@"C:\Program Files\Earshot"));

        (int exit, string output) = RunServiceProbe(service, json: false);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "service EarshotHandBack: running");
        StringAssert.Contains(output, "state running, start type 2");
        StringAssert.Contains(output, "image path \"C:\\Program Files\\Earshot\\Earshot.exe\" service");
    }

    [TestMethod]
    public void ProbeServiceExitsNonZeroOnlyWhenTheControlManagerCouldNotBeRead()
    {
        var service = new Earshot.Tests.Service.FakeServiceControl { Unreadable = true };

        (int exit, string output) = RunServiceProbe(service, json: true);

        Assert.AreEqual(ExitCodes.OsError, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.IsFalse(document.RootElement.GetProperty("present").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("readable").GetBoolean());
        Assert.AreEqual("unreadable: ERROR_ACCESS_DENIED", document.RootElement.GetProperty("summary").GetString());
    }
}
