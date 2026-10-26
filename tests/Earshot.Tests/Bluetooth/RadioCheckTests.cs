using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Bluetooth;

[TestClass]
public sealed class RadioCheckTests
{
    private sealed class FakeRadioApi(Func<CancellationToken, Task<AdapterReading>> read) : IRadioApi
    {
        public Task<AdapterReading> ReadAsync(CancellationToken ct) => read(ct);
    }

    private sealed class FakeNodes(DevNodeStatusInfo info) : IDevNodeStatusReader
    {
        public List<string> Asked { get; } = new();

        public DevNodeStatusInfo Read(string instanceId)
        {
            Asked.Add(instanceId);
            return info;
        }
    }

    private const string RadioNode = @"USB\VID_0000&PID_0000\fake";

    private static AdapterReading Adapter(int? radioState, bool found = true, bool classic = true, string? id = RadioNode) =>
        new(found, classic, radioState, id, found ? [] : [StepOutcomes.FromHResult("bluetooth-adapter", 0, "GetDefaultAsync returned no adapter.", ok: false)]);

    private static DevNodeStatusInfo Node(uint status = CfgMgr32.DN_STARTED, uint problem = 0, string? version = "23.60.0.2", DateOnly? date = null) =>
        new(true, status, problem, version, date ?? new DateOnly(2024, 3, 9), []);

    private static RadioCheck Check(AdapterReading reading, DevNodeStatusInfo node, TimeSpan? budget = null, FakeNodes? nodes = null) =>
        new(new FakeRadioApi(_ => Task.FromResult(reading)), nodes ?? new FakeNodes(node), budget);

    [TestMethod]
    public async Task AnAdapterWithClassicAnOnRadioAndNoProblemIsOkAndSaysNothing()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.On), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Ok, r.State);
        Assert.IsNull(r.Line);
        Assert.AreEqual(RadioAction.None, r.Action);
        Assert.IsFalse(r.BlocksRepair);
        Assert.AreEqual("23.60.0.2", r.DriverVersion);
        Assert.AreEqual("2024-03-09", r.DriverDate);
        Assert.AreEqual(0, r.HistoryCode);
        Assert.AreEqual(0, r.ProblemCode);
    }

    [TestMethod]
    public async Task AnOffRadioSaysBluetoothIsOffAndOffersBluetoothSettings()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.Off), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Off, r.State);
        Assert.AreEqual("Bluetooth is off.", r.Line);
        Assert.AreEqual(RadioAction.BluetoothSettings, r.Action);
        Assert.IsTrue(r.BlocksRepair);
        Assert.AreEqual(2, r.HistoryCode);
    }

    [TestMethod]
    public async Task ADisabledRadioIsSwitchedOff()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.Disabled), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.SwitchedOff, r.State);
        Assert.AreEqual("Bluetooth is turned off by a switch on this PC or by its firmware.", r.Line);
        Assert.AreEqual(RadioAction.BluetoothSettings, r.Action);
        Assert.AreEqual(3, r.HistoryCode);
    }

    [TestMethod]
    public async Task AProblemCodeIsReportedWithItsNumberWhateverTheRadioStateSays()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.On), Node(CfgMgr32.DN_HAS_PROBLEM, problem: 10)).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Problem, r.State);
        Assert.AreEqual("Bluetooth on this PC isn't working. Windows reports problem code 10.", r.Line);
        Assert.AreEqual(10, r.ProblemCode);
        Assert.AreEqual(10, r.HistoryCode);
        Assert.AreEqual(RadioAction.BluetoothSettings, r.Action);
        Assert.AreEqual(CfgMgr32.DN_HAS_PROBLEM, r.DevNodeStatus);
    }

    [TestMethod]
    public async Task AProblemNumberWithoutTheHasProblemBitIsNotAProblem()
    {
        // pulProblemNumber is meaningful only with DN_HAS_PROBLEM set.
        RadioCheckResult r = await Check(Adapter(RadioStateValues.On), Node(CfgMgr32.DN_STARTED, problem: 31)).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Ok, r.State);
        Assert.AreEqual(0, r.ProblemCode);
    }

    [TestMethod]
    public async Task NoAdapterReadsAsNoAdapter()
    {
        RadioCheckResult r = await Check(Adapter(null, found: false, id: null), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.NoAdapter, r.State);
        Assert.AreEqual("Windows finds no Bluetooth on this PC.", r.Line);
        Assert.AreEqual(RadioAction.BluetoothSettings, r.Action);
        Assert.IsTrue(r.BlocksRepair);
    }

    // An adapter that was found and does not do Classic Bluetooth is not "no Bluetooth": it has a line of its own, and no
    // button, since no setting changes it.
    [TestMethod]
    public async Task AnAdapterWithoutClassicBluetoothSaysSoAndOffersNoButton()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.On, classic: false), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.NoClassic, r.State);
        Assert.AreEqual("Bluetooth on this PC can't do Classic Bluetooth, which AirPods need for sound.", r.Line);
        Assert.AreEqual(RadioAction.None, r.Action);
        Assert.IsTrue(r.BlocksRepair);
    }

    // A default adapter that could not be asked (the call threw, or Windows is too old) says nothing about whether there is an
    // adapter: no line, no block of the offers, and the raw code kept.
    [TestMethod]
    public async Task AnAdapterReadThatDidNotAnswerClaimsNothingAndKeepsItsCode()
    {
        var threw = new AdapterReading(false, false, null, null, [StepOutcomes.FromHResult("bluetooth-adapter", unchecked((int)0x8007048F), "COMException", ok: false)], Answered: false);

        RadioCheckResult r = await Check(threw, Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Unknown, r.State);
        Assert.IsNull(r.Line);
        Assert.AreEqual(RadioAction.None, r.Action);
        Assert.IsFalse(r.BlocksRepair);
        Assert.AreEqual(unchecked((int)0x8007048F), r.HistoryCode);
    }

    [TestMethod]
    public async Task AnUnknownRadioStateClaimsNothing()
    {
        RadioCheckResult r = await Check(Adapter(RadioStateValues.Unknown), Node()).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Unknown, r.State);
        Assert.IsNull(r.Line);
        Assert.AreEqual(RadioAction.None, r.Action);
        Assert.IsFalse(r.BlocksRepair, "Unknown never stops a Fix offer.");
    }

    [TestMethod]
    public async Task AnOnRadioWhoseNodeCouldNotBeReadIsUnknownNotOk()
    {
        var unread = new DevNodeStatusInfo(false, 0, 0, null, null, [StepOutcomes.FromConfigRet("cm-locate-radio", CfgMgr32.CR_NO_SUCH_DEVNODE)]);

        RadioCheckResult r = await Check(Adapter(RadioStateValues.On), unread).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Unknown, r.State);
        Assert.IsTrue(r.ReadCodes.Any(s => s.Step == "cm-locate-radio" && !s.Ok), "The raw CONFIGRET is kept.");
        Assert.IsNull(r.DevNodeStatus);
    }

    [TestMethod]
    public async Task AnOnRadioWithNoInstanceIdIsUnknownAndNoNodeIsRead()
    {
        var nodes = new FakeNodes(Node());

        RadioCheckResult r = await Check(Adapter(RadioStateValues.On, id: null), Node(), nodes: nodes).CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Unknown, r.State);
        Assert.IsEmpty(nodes.Asked);
    }

    [TestMethod]
    public async Task AReadThatDoesNotFinishInTheBudgetEndsUnknownWithATimeoutCode()
    {
        CancellationToken seen = default;
        var api = new FakeRadioApi(async ct =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Adapter(RadioStateValues.On);
        });
        var check = new RadioCheck(api, new FakeNodes(Node()), TimeSpan.FromMilliseconds(80));

        RadioCheckResult r = await check.CheckAsync(CancellationToken.None);

        Assert.AreEqual(RadioCheckState.Unknown, r.State);
        Assert.IsTrue(r.ReadCodes.Any(s => s.Step == "radio-read" && s.Code == 1460 && !s.Ok), string.Join("; ", r.ReadCodes.Select(s => s.Step + " " + s.Code)));
        Assert.IsLessThan(2000, r.ElapsedMs);
        Assert.IsTrue(seen.IsCancellationRequested, "The abandoned read is told to stop.");
    }

    [TestMethod]
    public async Task TheCheckIsTimedInTheLog()
    {
        var log = new CapturingLog();
        var check = new RadioCheck(new FakeRadioApi(_ => Task.FromResult(Adapter(RadioStateValues.Off))), new FakeNodes(Node()), null, log);

        await check.CheckAsync(CancellationToken.None);

        Assert.IsTrue(log.Has(LogLevel.Info, "radio check: Off in "), string.Join("; ", log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task ACancelledCheckThrowsRatherThanReportingAState()
    {
        using var cts = new CancellationTokenSource();
        var api = new FakeRadioApi(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Adapter(RadioStateValues.On);
        });
        var check = new RadioCheck(api, new FakeNodes(Node()), TimeSpan.FromSeconds(30));
        cts.CancelAfter(50);

        await Assert.ThrowsAsync<OperationCanceledException>(() => check.CheckAsync(cts.Token));
    }

    [TestMethod]
    public void TheHistoryStepNamesTheStateAndCarriesItsCode()
    {
        var r = new RadioCheckResult(RadioCheckState.Problem, "x", RadioAction.BluetoothSettings, true, true, 1, 0x400, 43, null, null, [], 5);

        StepOutcome step = r.ToStep();

        Assert.AreEqual("radio-check", step.Step);
        Assert.AreEqual(43, step.Code);
        Assert.AreEqual("Problem", step.CodeName);
        Assert.IsFalse(step.Ok);
    }

    // The one real run of the read-only path: this PC's adapter, radio and radio node, with nothing changed. It asserts what holds
    // on any PC; the exact state is what a live run looks at.
    [TestMethod]
    public async Task TheRealReadsRunReadOnlyAndGiveSomeStateWithCodes()
    {
        RadioCheck check = RadioCheck.CreateReal();

        RadioCheckResult r = await check.CheckAsync(CancellationToken.None);

        Console.WriteLine("radio: " + r.State + " adapter=" + r.AdapterFound + " classic=" + r.ClassicSupported + " radioState=" + r.RadioState +
            " status=" + r.DevNodeStatus + " problem=" + r.ProblemCode + " driver=" + r.DriverVersion + " " + r.DriverDate + " ms=" + r.ElapsedMs +
            " codes=" + string.Join(",", r.ReadCodes.Select(s => s.Step + ":" + s.CodeName)));
        Assert.AreNotEqual(RadioCheckState.Unreachable, r.State, "Unreachable comes from a pairing result, never from these reads.");
        Assert.IsNotEmpty(r.ReadCodes);
        if (r.AdapterFound == true)
        {
            Assert.IsNotNull(r.RadioState);
        }
    }
}
