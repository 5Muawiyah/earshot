using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The seams of Fix connection as fakes. Every one records its calls, so a test can say what was asked and what was not.

internal sealed class FakeDesktop : IInputDesktop
{
    public DesktopReading Reading { get; set; } = new(UserInteractive: true, ThreadDesktop: "Default", InputDesktop: "Default", InputError: 0);

    public int Reads { get; private set; }

    public DesktopReading Read()
    {
        Reads++;
        return Reading;
    }

    public static FakeDesktop Interactive() => new();

    public static FakeDesktop NoOneThere() => new()
    {
        Reading = new DesktopReading(UserInteractive: true, ThreadDesktop: "PrivateDesktop", InputDesktop: "Default", InputError: 0),
    };
}

internal sealed class FakePrompter : IFixPrompter
{
    public Queue<FixPromptAnswer> Answers { get; } = new();

    public List<FixPrompt> Asked { get; } = new();

    // Runs as a question is asked, before it is answered.
    public Action<FixPrompt>? OnAsk { get; set; }

    // The token each question was asked with (CancellationToken.None for the plain Ask), in order.
    public List<CancellationToken> Tokens { get; } = new();

    public FixPromptAnswer Ask(FixPrompt prompt, CancellationToken closeOn)
    {
        Tokens.Add(closeOn);
        return Answer(prompt);
    }

    public FixPromptAnswer Ask(FixPrompt prompt)
    {
        Tokens.Add(CancellationToken.None);
        return Answer(prompt);
    }

    private FixPromptAnswer Answer(FixPrompt prompt)
    {
        Asked.Add(prompt);
        OnAsk?.Invoke(prompt);
        return Answers.Count > 0 ? Answers.Dequeue() : new FixPromptAnswer(FixAnswer.Cancel, -1);
    }

    public void SayOk(int chosen = -1) => Answers.Enqueue(new FixPromptAnswer(FixAnswer.Ok, chosen));

    public void SayCancel() => Answers.Enqueue(new FixPromptAnswer(FixAnswer.Cancel, -1));

    public void SayTimedOut() => Answers.Enqueue(new FixPromptAnswer(FixAnswer.TimedOut, -1));
}

// Runs the elevated side in this process against a rig, or just counts.
internal sealed class FakeElevator : IFixElevator
{
    public List<(string Path, string Sha256)> Calls { get; } = new();

    public Func<string, string, ElevatedRun> Result { get; set; } = (_, _) => new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0));

    public Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        Calls.Add((planPath, planSha256));
        return Task.FromResult(Result(planPath, planSha256));
    }
}

internal sealed class FakeGate : IFixGate
{
    public List<string> Calls { get; } = new();

    public Func<FixGateResult> BlockResult { get; set; } = () => new FixGateResult(true, "Blocked", [], 0);

    public Func<string, FixGateResult> AdoptResult { get; set; } = _ => new FixGateResult(true, "Adopted", [], 0);

    public Action<string>? OnCall { get; set; }

    public Task<FixGateResult> BlockAsync(CancellationToken ct)
    {
        Calls.Add("block");
        OnCall?.Invoke("block");
        return Task.FromResult(BlockResult());
    }

    public Task<FixGateResult> AdoptDeviceAsync(string address12, CancellationToken ct)
    {
        Calls.Add("adopt:" + address12);
        OnCall?.Invoke("adopt");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(AdoptResult(address12));
    }
}

internal sealed class FakeRePairUi : IFixRePairUi
{
    public List<string> Shown { get; } = new();

    public int Pick { get; set; }

    public bool ConfirmPin { get; set; } = true;

    public List<string> PinsShown { get; } = new();

    // Runs as the pick is asked, before it is answered.
    public Action? OnPick { get; set; }

    public void ShowInstruction(string text) => Shown.Add(text);

    // How long the pick stays up before it closes unanswered, as the real dialog does at its time limit; zero answers at once.
    public TimeSpan HoldPick { get; set; }

    // Ends a held pick early (still unanswered) once it completes, so a test that acts while the pick is up does not race the
    // hold on real time.
    public Task? HoldPickUntil { get; set; }

    public async Task<PickAnswer> PickAsync(IReadOnlyList<UnpairedDevice> candidates, CancellationToken ct)
    {
        OnPick?.Invoke();
        Shown.Add("pick:" + string.Join("|", candidates.Select(c => c.Name)));
        if (HoldPick > TimeSpan.Zero)
        {
            await Task.WhenAny(Task.Delay(HoldPick, ct), HoldPickUntil ?? Task.Delay(Timeout.Infinite, ct));
            if (ct.IsCancellationRequested)
            {
                return new PickAnswer(null, FixAnswer.Cancel);
            }

            return new PickAnswer(null, FixAnswer.TimedOut);
        }

        return Pick >= 0 && Pick < candidates.Count ? new PickAnswer(candidates[Pick], FixAnswer.Ok) : new PickAnswer(null, FixAnswer.Cancel);
    }

    // The token each number question was given, so a test can see it ends with the pairing attempt.
    public List<CancellationToken> PinTokens { get; } = new();

    // Called while the number question is up, before its answer.
    public Action? OnPin { get; set; }

    public Task<bool> ConfirmPinAsync(FixPairKind kind, string? pin, CancellationToken ct)
    {
        PinsShown.Add(kind + ":" + pin);
        PinTokens.Add(ct);
        OnPin?.Invoke();
        return Task.FromResult(ConfirmPin);
    }

    // The trace button's answer when it is offered.
    public bool PressTrace { get; set; }

    public bool ShowResult(string text, bool offerTrace)
    {
        Shown.Add("result:" + text + (offerTrace ? " [trace offered]" : ""));
        return offerTrace && PressTrace;
    }

    public void Close() => Shown.Add("closed");
}

// Delegates to a node table a test can replace, so nodes can appear when a pairing "completes".
internal sealed class SwitchableNodes : INodeApi
{
    public SwitchableNodes(FakeNodeApi inner) => Inner = inner;

    public FakeNodeApi Inner { get; set; }

    public uint ListDeviceIds(out string[] ids) => Inner.ListDeviceIds(out ids);

    public uint Locate(string instanceId, bool includeNonPresent, out uint devInst) => Inner.Locate(instanceId, includeNonPresent, out devInst);

    public uint GetStatus(uint devInst, out uint status, out uint problem) => Inner.GetStatus(devInst, out status, out problem);

    public uint GetContainerId(uint devInst, out Guid containerId) => Inner.GetContainerId(devInst, out containerId);

    public uint GetConfigFlags(uint devInst, out uint configFlags) => Inner.GetConfigFlags(devInst, out configFlags);

    public uint GetName(uint devInst, out string? name) => Inner.GetName(devInst, out name);

    public uint Disable(uint devInst, uint flags) => Inner.Disable(devInst, flags);

    public uint Enable(uint devInst) => Inner.Enable(devInst);

    public Earshot.Interop.ConfigFlagsChange ClearDisabledFlag(string instanceId) => Inner.ClearDisabledFlag(instanceId);

    public Earshot.Interop.ConfigFlagsChange SetDisabledFlag(string instanceId) => Inner.SetDisabledFlag(instanceId);

    public bool Pause(TimeSpan delay) => Inner.Pause(delay);
}

internal sealed class FakePairing : IPairingApi
{
    public List<string> Calls { get; } = new();

    public List<PairedDevice> Paired { get; } = new();

    // The steps the paired list reports with it, so a test can make the read fail.
    public List<StepOutcome> ListSteps { get; } = new();

    public List<WatchEvent> Events { get; } = new();

    public PairingCallResult UnpairResult { get; set; } = new("Unpaired", 0, true, null);

    public PairingCallResult PairResult { get; set; } = new("Paired", 0, true, null);

    public bool UnpairRemovesFromList { get; set; } = true;

    // Raised through the prompt while pairing runs.
    public List<(FixPairKind Kind, string? Pin)> Requests { get; } = new();

    public List<bool> RequestAnswers { get; } = new();

    // Runs when a pairing succeeds: the test adds the new device to the paired list and its nodes to the table.
    public Action<string>? OnPaired { get; set; }

    public Action? OnUnpaired { get; set; }

    public Exception? ThrowOnPair { get; set; }

    // PairAsync waits until it is cancelled, as a pairing that never answers does.
    public bool HangOnPair { get; set; }

    // Runs as the paired list is read, before it is answered.
    public Action? OnListPaired { get; set; }

    // The paired list never answers, until the token says stop.
    public bool HangOnList { get; set; }

    // Thrown by the paired list after a short wait, so the caller has already gone past its first await, as a real WinRT read is.
    public Exception? ThrowOnListLater { get; set; }

    public async Task<PairedListResult> ListPairedAsync(CancellationToken ct)
    {
        Calls.Add("list-paired");
        OnListPaired?.Invoke();
        if (ThrowOnListLater is { } later)
        {
            await Task.Delay(200, ct);
            throw later;
        }

        if (HangOnList)
        {
            await Hang(ct);
        }

        return new PairedListResult(Paired.ToList(), ListSteps.ToList());
    }

    // UnpairAsync waits until it is cancelled, as a removal Windows never answers does.
    public bool HangOnUnpair { get; set; }

    // A hang that ignores its token, as a WinRT operation that does not honour Cancel would: the call never ends.
    public bool HangIgnoresCancel { get; set; }

    private Task Hang(CancellationToken ct) => HangIgnoresCancel ? new TaskCompletionSource().Task : Task.Delay(Timeout.Infinite, ct);

    public async Task<PairingCallResult> UnpairAsync(string deviceId, CancellationToken ct)
    {
        Calls.Add("unpair:" + deviceId);
        if (HangOnUnpair)
        {
            await Hang(ct);
        }

        if (UnpairResult.Success)
        {
            if (UnpairRemovesFromList)
            {
                Paired.RemoveAll(p => p.Id == deviceId);
            }

            OnUnpaired?.Invoke();
        }

        return UnpairResult;
    }

    public IUnpairedWatch Watch()
    {
        Calls.Add("watch");
        return new ScriptedWatch(Events);
    }

    // Runs as PairAsync is called, before anything else it does.
    public Action? OnPair { get; set; }

    public async Task<PairingCallResult> PairAsync(string deviceId, IPairingPrompt prompt, CancellationToken ct)
    {
        Calls.Add("pair:" + deviceId);
        OnPair?.Invoke();
        if (ThrowOnPair is not null)
        {
            throw ThrowOnPair;
        }

        if (HangOnPair)
        {
            await Hang(ct);
        }

        foreach ((FixPairKind kind, string? pin) in Requests)
        {
            RequestAnswers.Add(await prompt.ConfirmAsync(kind, pin));
        }

        if (PairResult.Success)
        {
            OnPaired?.Invoke(deviceId);
        }

        return PairResult;
    }

    private sealed class ScriptedWatch(List<WatchEvent> events) : IUnpairedWatch
    {
        public async IAsyncEnumerable<WatchEvent> EventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (WatchEvent e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
                await Task.Yield();
            }
        }

        public void Dispose()
        {
        }
    }
}

// Everything a Fix run needs, over a fake node table and a real GateStore and plan folder in a temp folder.
internal sealed class FixRig : IDisposable
{
    private readonly TempFolder _temp = new();

    public const string SecondAirPodsAddress = "0A1B2C3D4E8D";
    public static readonly Guid SecondAirPodsContainer = new("11111111-2222-4333-8444-555555555501");
    public const string NewAirPodsAddress = "0A1B2C3D4E8E";
    public static readonly Guid NewAirPodsContainer = new("11111111-2222-4333-8444-555555555502");

    public FixRig(FakeNodeApi? nodes = null, bool pinned = true)
    {
        Gate = new GateRig(nodes);
        Nodes = new SwitchableNodes(Gate.Nodes);
        if (pinned)
        {
            Gate.Pin(RecordedNodes.AirPods());
        }

        Records = new StoreFixRecords(Gate.Store, () => File.Exists(SettingsFile));
        PlanFiles = new FixPlanFiles(Path.Combine(_temp.Path, "fix"));
        Recovery = new FixRecovery(RecoveryFile, Gate.Time, _ => true);
        Desktop = FakeDesktop.Interactive();
        Output = new StringWriter();
    }

    public GateRig Gate { get; }

    public SwitchableNodes Nodes { get; }

    public IFixRecords Records { get; set; }

    public FixPlanFiles PlanFiles { get; }

    public FixRecovery Recovery { get; }

    public FakeDesktop Desktop { get; set; }

    public FakePrompter Prompter { get; } = new();

    public FakeElevator Elevator { get; } = new();

    // In place of the fake elevator when a test needs another kind (the one that says to set up Earshot first).
    public IFixElevator? ElevatorOverride { get; set; }

    // The trace request the result window's button makes; null for a host with no tray to ask.
    public Func<bool>? RequestTrace { get; set; }

    public FakePairing Pairing { get; } = new();

    public FakeRePairUi Ui { get; } = new();

    public FakeGate GateFake { get; } = new();

    public StringWriter Output { get; }

    public CapturingLog Log { get; } = new();

    public string SettingsFile => Path.Combine(_temp.Path, "settings.json");

    public string RecoveryFile => Path.Combine(_temp.Path, "fix-window.json");

    public StubLeftovers Leftovers { get; } = new();

    public IRadioCheck? Radio { get; set; }

    public ConnectHistory? History { get; set; }

    public FixHost Host() => new()
    {
        Nodes = Nodes,
        Records = Records,
        Leftovers = Leftovers,
        Desktop = Desktop,
        Prompter = Prompter,
        PlanFiles = PlanFiles,
        Elevator = ElevatorOverride ?? Elevator,
        Pairing = Pairing,
        RePairUi = Ui,
        Gate = GateFake,
        Recovery = Recovery,
        Output = Output,
        Log = Log,
        Radio = Radio,
        History = History,
        RequestTrace = RequestTrace,
        Options = new FixOptions
        {
            BaselineWait = TimeSpan.FromMilliseconds(50),
            ScanTimeout = TimeSpan.FromMilliseconds(200),
            SettleWait = TimeSpan.FromMilliseconds(20),
            PairTimeout = TimeSpan.FromMilliseconds(300),
            PostPairWait = TimeSpan.Zero,
            PairedListWait = TimeSpan.FromMilliseconds(200),
            UnpairWait = TimeSpan.FromMilliseconds(200),
            Delay = (_, _) => Task.CompletedTask,
        },
    };

    // Every node of the AirPods disabled with the persistent flag, with the audio nodes out of the tree (the shape the
    // owner's PC showed), so the plan has both kinds of step.
    public void LeaveAirPodsBlocked()
    {
        Gate.BlockAirPodsAtRest();
        foreach (int index in new[] { 4, 6, 7, 8 })
        {
            Gate.Nodes[RecordedNodes.AirPodsTargets[index]].Present = false;
        }
    }

    public static FakeNode[] SecondAirPodsNodes() =>
        [
            new FakeNode(@"BTHENUM\DEV_" + SecondAirPodsAddress + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + SecondAirPodsAddress, SecondAirPodsContainer, "Second AirPods"),
            new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&" + SecondAirPodsAddress + "_C00000000", SecondAirPodsContainer),
        ];

    public static FakeNode[] NewAirPodsNodes() =>
        [
            new FakeNode(@"BTHENUM\DEV_" + NewAirPodsAddress + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + NewAirPodsAddress, NewAirPodsContainer, "New AirPods"),
            new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&" + NewAirPodsAddress + "_C00000000", NewAirPodsContainer),
        ];

    public void Dispose()
    {
        Gate.Dispose();
        _temp.Dispose();
    }
}

internal static class Expect
{
    // The sequence equals the expected values, in order.
    public static void Same<T>(IEnumerable<T> actual, params T[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToList());
}

internal sealed class StubLeftovers : IFixLeftovers
{
    public List<string> Items { get; } = new();

    public IReadOnlyList<string> Read() => Items.ToList();
}

internal sealed class FakeRadioCheck(RadioCheckResult result) : IRadioCheck
{
    public int Checks { get; private set; }

    // How long a read takes, as a slow radio would.
    public TimeSpan Wait { get; set; }

    // Called with the read's number as each read starts, so a test can make one read slow.
    public Action<int>? OnCheck { get; set; }

    // A read that does not answer until this completes, whatever its token says, as a radio read that ignores a cancel.
    public Task? HoldUntil { get; set; }

    public async Task<RadioCheckResult> CheckAsync(CancellationToken ct)
    {
        Checks++;
        OnCheck?.Invoke(Checks);
        if (Wait > TimeSpan.Zero)
        {
            await Task.Delay(Wait, ct);
        }

        if (HoldUntil is { } hold)
        {
            await hold;
        }

        return result;
    }
}
