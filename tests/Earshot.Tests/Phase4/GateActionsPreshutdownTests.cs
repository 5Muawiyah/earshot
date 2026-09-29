using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The shut-down block the hand-back service runs, over the recorded fake node table in a temp folder. It disables
// and never enables, reads its settings only from the checked machine folder, and does nothing when the nodes are
// blocked already, when either setting is off, or when it cannot trust what it reads.
[TestClass]
public sealed class GateActionsPreshutdownTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string SinkNode = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000";

    // The machine folder with an access list a standard user could write to.
    private const string UsersCanWrite = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness(bool writeConfig = true)
        {
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Machine);
            Store = new GateStore(Machine);
            Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            if (writeConfig)
            {
                Assert.IsTrue(Store.WriteConfig(new GateConfig { HandBackAtShutdown = true }).Ok);
            }

            Started = Time.GetUtcNow();
        }

        public string Machine { get; }

        public GateStore Store { get; }

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public FakeFolderSecurity Folders { get; } = new();

        public CapturingLog Log { get; } = new();

        public ManualTime Time { get; } = new();

        public DateTimeOffset Started { get; }

        public DateTimeOffset Deadline => Started + PreshutdownBudget.Total;

        public IGateRunLock? RunLock { get; set; }

        // The node API the run uses, when it is not the fake table itself.
        public INodeApi? NodeApi { get; set; }

        public Func<TimeSpan, bool>? LockWait { get; set; }

        public Func<TimeSpan, bool>? RetryWait { get; set; }

        public PreshutdownResult Run(DateTimeOffset? deadline = null)
        {
            var actions = new GateActions(NodeApi ?? Nodes, Store, Folders, Log, Time, RunLock)
            {
                LockWait = LockWait ?? DeviceChangeLock.SleepAndContinue,
                RetryWait = RetryWait ?? (_ => true),
            };
            return actions.RunPreshutdown(Nonce, deadline ?? Deadline);
        }

        public GateStatusFile Status()
        {
            GateRead<GateStatusFile> read = Store.ReadStatus(Nonce);
            Assert.IsTrue(read.IsOk, read.Step.Detail);
            return read.Value!;
        }

        public bool StatusFileExists => File.Exists(Store.StatusFile(Nonce));

        public void BlockAll()
        {
            foreach (string id in RecordedNodes.AirPodsTargets)
            {
                Nodes[id].MarkDisabled(persistent: true);
            }
        }

        public int Disables => Nodes.Calls.Count(c => c.Kind == "disable");

        public void Dispose() => _temp.Dispose();
    }

    // A run lock that is entered, but only after it was waited for the whole time it was given.
    private sealed class AdvancingRunLock(ManualTime time) : IGateRunLock
    {
        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            time.Advance(timeout);
            return new NoDispose();
        }

        private sealed class NoDispose : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    // The fake node table, where disabling one node costs time.
    private sealed class SlowNodes(FakeNodeApi inner, ManualTime time, string instanceId, TimeSpan cost) : INodeApi
    {
        public uint ListDeviceIds(out string[] ids) => inner.ListDeviceIds(out ids);

        public uint Locate(string id, bool includeNonPresent, out uint devInst) => inner.Locate(id, includeNonPresent, out devInst);

        public uint GetStatus(uint devInst, out uint status, out uint problem) => inner.GetStatus(devInst, out status, out problem);

        public uint GetContainerId(uint devInst, out Guid containerId) => inner.GetContainerId(devInst, out containerId);

        public uint GetConfigFlags(uint devInst, out uint configFlags) => inner.GetConfigFlags(devInst, out configFlags);

        public uint GetName(uint devInst, out string? name) => inner.GetName(devInst, out name);

        public uint Disable(uint devInst, uint flags)
        {
            uint result = inner.Disable(devInst, flags);
            if (inner.Calls[^1].InstanceId == instanceId)
            {
                time.Advance(cost);
            }

            return result;
        }

        public uint Enable(uint devInst) => inner.Enable(devInst);
    }

    private sealed class BusyRunLock : IGateRunLock
    {
        public List<TimeSpan> Waits { get; } = new();

        public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
        {
            Waits.Add(timeout);
            steps.Add(StepOutcomes.FromWin32(MachineGateMutex.StepName, MachineGateMutex.WaitTimeout, "held elsewhere", ok: false));
            return null;
        }
    }

    [TestMethod]
    public void NodesBlockedAlreadyMakeNoCallAndSayAlreadyBlocked()
    {
        using var h = new Harness();
        h.BlockAll();

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual(nameof(BlockState.Blocked), result.State);
        Assert.AreEqual("already blocked", result.Reason);
        Assert.IsFalse(result.BlockSent);
        Assert.IsEmpty(h.Nodes.Calls, "A blocked device is not touched again.");
        GateStatusFile status = h.Status();
        Assert.AreEqual(GateVerbs.Preshutdown, status.Verb);
        Assert.AreEqual("success", status.Result);
        Assert.AreEqual(nameof(BlockState.Blocked), status.State);
        Assert.AreEqual("already blocked", status.Steps.Single(s => s.Step == "preshutdown").Detail);
    }

    [TestMethod]
    public void EnabledNodesAreDisabledPersistentlyWhenBothSettingsAreOn()
    {
        using var h = new Harness();

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual(nameof(BlockState.Blocked), result.State);
        Assert.IsTrue(result.BlockSent);
        Assert.AreEqual(9, h.Disables);
        Assert.IsTrue(h.Nodes.Calls.All(c => c.Kind == "disable" && c.Flags == GateActions.BlockDisableFlags), "Persistent, on every node.");
        Assert.AreEqual(RecordedNodes.AirPodsDeviceNode, h.Nodes.Calls[^1].InstanceId, "The device node goes last.");
        GateStatusFile status = h.Status();
        Assert.AreEqual(GateVerbs.Preshutdown, status.Verb);
        Assert.AreEqual(h.Started, status.StartedUtc, "Started is when the control arrived.");
        StringAssert.StartsWith(status.Steps.Single(s => s.Step == "preshutdown").Detail, "block sent at ");
        Assert.HasCount(9, status.Steps.Where(s => s.Step.StartsWith("cm-disable:", StringComparison.Ordinal) && s.Ok));
    }

    // A config.json written before the member existed has no hand-back setting, which reads as off.
    [TestMethod]
    public void AConfigWithoutTheHandBackMemberChangesNothing()
    {
        using var h = new Harness(writeConfig: false);
        File.WriteAllText(h.Store.ConfigFile, "{\"SchemaVersion\":1,\"BlockAtBoot\":true}");

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual("Hand back is off", result.Reason);
        Assert.AreEqual(0, h.Disables);
    }

    [TestMethod]
    public void HandBackOffChangesNothingAndSaysSo()
    {
        using var h = new Harness();
        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = true, HandBackAtShutdown = false }).Ok);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual("Hand back is off", result.Reason);
        Assert.IsFalse(result.BlockSent);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual("Hand back is off", h.Status().Steps.Single(s => s.Step == "preshutdown").Detail);
    }

    [TestMethod]
    public void BlockAtBootOffChangesNothingAndSaysSo()
    {
        using var h = new Harness();
        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = false, HandBackAtShutdown = true }).Ok);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual("Block at boot is off", result.Reason);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void AMissingConfigIsNotSetUpAndChangesNothing()
    {
        using var h = new Harness(writeConfig: false);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.NoConfig, result.Outcome);
        Assert.AreEqual("not set up", result.Reason);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    // A setting that cannot be read is never taken as permission.
    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":\"true\"}")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":true,\"HandBackAtShutdown\":\"true\"}")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":true,\"Extra\":1}")]
    public void AnInvalidConfigChangesNothing(string content)
    {
        using var h = new Harness(writeConfig: false);
        File.WriteAllText(h.Store.ConfigFile, content);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual("config.json is not valid", result.Reason);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"Address\":\"0A1B2C3D4E8C\"}")]
    [DataRow("{\"Address\":\"0a1b2c3d4e8c\",\"ContainerId\":\"5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13\"}")]
    [DataRow("{\"Address\":\"0A1B2C3D4E8C\",\"ContainerId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public void AnInvalidOrMissingDeviceFileChangesNothing(string content)
    {
        using var h = new Harness();
        File.WriteAllText(h.Store.DeviceFile, content);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.NoIdentity, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);

        File.Delete(h.Store.DeviceFile);
        Assert.AreEqual(GateExitCode.NoIdentity, h.Run().Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void AnOversizeDeviceFileIsNotReadAndChangesNothing()
    {
        using var h = new Harness();
        File.WriteAllText(h.Store.DeviceFile, "{\"Address\":\"0A1B2C3D4E8C\",\"ContainerId\":\"5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13\"}" + new string(' ', GateStore.MaxFileBytes));

        Assert.AreEqual(GateExitCode.NoIdentity, h.Run().Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    // The machine folder is checked before anything in it is read, so a folder a standard user could write to is never
    // trusted: nothing is read from it, no node is touched and no status file is written into it.
    [TestMethod]
    [DataRow(UsersCanWrite, "Users can write")]
    [DataRow("O:S-1-5-21-1-2-3-1001G:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", "the owner is a standard user")]
    [DataRow("O:BAG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", "the list is not protected")]
    [DataRow("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x2;;;AU)", "authenticated users can write")]
    [DataRow("O:BAG:SYD:NO_ACCESS_CONTROL", "no access list at all")]
    public void AFolderAStandardUserCouldWriteIsNeverTrusted(string sddl, string why)
    {
        using var h = new Harness();
        h.Folders.DefaultMachineSddl = sddl;

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome, why);
        Assert.IsEmpty(h.Nodes.Calls, why);
        Assert.IsFalse(h.StatusFileExists, why + ": nothing is written into a folder that failed its check.");
        Assert.IsFalse(result.StatusWritten);
    }

    [TestMethod]
    public void AFolderWhoseAccessListCannotBeReadIsNeverTrusted()
    {
        using var h = new Harness();
        Directory.Delete(h.Machine, recursive: true);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(Directory.Exists(h.Machine), "The run does not create the folder.");
    }

    [TestMethod]
    public void AListThatCannotBeReadFromTheNodesChangesNothing()
    {
        using var h = new Harness();
        h.Nodes.ListResult = CfgMgr32.CR_FAILURE;

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual("the nodes could not be read", result.Reason);
        Assert.AreEqual(nameof(BlockState.Unknown), result.State);
        Assert.IsEmpty(h.Nodes.Calls, "A read that failed is not 'not blocked'.");
    }

    [TestMethod]
    public void ARunLockHeldElsewhereChangesNothingAndPrunesNothing()
    {
        using var h = new Harness();
        var busy = new BusyRunLock();
        h.RunLock = busy;
        // A status file from an earlier run: with the lock not held nothing is pruned.
        for (int i = 0; i < 20; i++)
        {
            string old = h.Store.StatusFile(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(old, "{}");
        }

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual("another Earshot run held the lock", result.Reason);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.AreEqual(PreshutdownBudget.RunLockWait, busy.Waits.Single(), "Waits its own limit while the budget allows.");
        Assert.AreEqual(21, Directory.GetFiles(h.Machine, "status-*.json").Length, "No prune without the lock (20 old files and this run's).");
        Assert.AreEqual("another Earshot run held the lock", h.Status().Steps.Single(s => s.Step == "preshutdown").Detail);
    }

    [TestMethod]
    public void TheRunLockWaitIsWhatRemainsOfTheBudgetAndNeverMore()
    {
        using var h = new Harness();
        var busy = new BusyRunLock();
        h.RunLock = busy;

        h.Time.Advance(TimeSpan.FromMilliseconds(1_000));
        PreshutdownResult late = h.Run();
        h.Time.Advance(TimeSpan.FromMilliseconds(5_000));
        PreshutdownResult over = h.Run();

        // The room the block needs is kept out of what may be waited: 8,000 less 1,000 gone less 5,500 reserved.
        Assert.AreEqual(TimeSpan.FromMilliseconds(1_500), busy.Waits[0]);
        Assert.AreEqual(TimeSpan.Zero, busy.Waits[1], "A budget with no room left over waits not at all.");
        Assert.AreEqual("the run lock", late.CutShortWaitingFor);
        Assert.AreEqual("the run lock", over.CutShortWaitingFor);
        Assert.IsEmpty(h.Nodes.Calls);
    }

    [TestMethod]
    public void ADeviceChangeLockHeldElsewhereIsWaitedForInsideTheBudgetOnly()
    {
        using var h = new Harness();
        using FileStream holder = new(Path.Combine(h.Machine, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        var polls = new List<TimeSpan>();
        h.LockWait = delay =>
        {
            polls.Add(delay);
            h.Time.Advance(delay);
            return true;
        };

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.AreEqual("another device change held the lock", result.Reason);
        Assert.AreEqual("the device change lock", result.CutShortWaitingFor);
        Assert.IsEmpty(h.Nodes.Calls);
        TimeSpan waited = h.Time.GetUtcNow() - h.Started;
        Assert.IsTrue(waited <= PreshutdownBudget.Total - PreshutdownBudget.BlockReserve, "Waited " + waited + ", which leaves the block no time.");
        Assert.IsTrue(waited > TimeSpan.FromSeconds(2), "It does wait for the lock, up to what the budget leaves: " + waited);
        Assert.IsFalse(polls.Any(p => p != DeviceChangeLock.PollInterval));
    }

    [TestMethod]
    public void AFailedBlockRecordsEachNodesCodeAndTheOutcomeIsPartial()
    {
        using var h = new Harness();
        h.Nodes[RecordedNodes.AirPodsTargets[0]].DisableResult = CfgMgr32.CR_NOT_DISABLEABLE;
        h.Nodes[RecordedNodes.AirPodsTargets[1]].DisableResult = CfgMgr32.CR_ACCESS_DENIED;

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual("partial", h.Status().Result);
        GateStatusFile status = h.Status();
        StepOutcome first = status.Steps.Single(s => s.Step == "cm-disable:" + RecordedNodes.AirPodsTargets[0]);
        StepOutcome second = status.Steps.Single(s => s.Step == "cm-disable:" + RecordedNodes.AirPodsTargets[1]);
        Assert.IsFalse(first.Ok);
        Assert.AreEqual((int)CfgMgr32.CR_NOT_DISABLEABLE, first.Code);
        Assert.AreEqual("CR_NOT_DISABLEABLE", first.CodeName);
        Assert.IsFalse(second.Ok);
        Assert.AreEqual((int)CfgMgr32.CR_ACCESS_DENIED, second.Code);
        Assert.AreEqual("CR_ACCESS_DENIED", second.CodeName);
        Assert.HasCount(7, status.Steps.Where(s => s.Step.StartsWith("cm-disable:", StringComparison.Ordinal) && s.Ok));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "CR_NOT_DISABLEABLE"), "Each failed step is logged.");
    }

    // A driver that refuses one node while it renders: the sink node was refused with the other seven and the device
    // node disabled. Sent again a moment later, once, and only that node.
    [TestMethod]
    public void AVetoedNodeIsSentAgainOnceAfterTheDelayAndTheBlockSucceeds()
    {
        using var h = new Harness();
        FakeNode sink = h.Nodes[SinkNode];
        sink.DisableResult = CfgMgr32.CR_REMOVE_VETOED;
        var waits = new List<TimeSpan>();
        h.RetryWait = delay =>
        {
            waits.Add(delay);
            sink.DisableResult = CfgMgr32.CR_SUCCESS;
            return true;
        };

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.AreEqual(nameof(BlockState.Blocked), result.State);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1_000), waits.Single());
        Assert.AreEqual(1, result.VetoedNodes);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1_000), result.RetryAfter);
        Assert.AreEqual(10, h.Disables, "Nine nodes, then the one that was refused, once more.");
        Assert.HasCount(2, h.Nodes.Calls.Where(c => c.InstanceId == SinkNode));
        StepOutcome[] sinkSteps = h.Status().Steps.Where(s => s.Step == "cm-disable:" + SinkNode).ToArray();
        Assert.HasCount(2, sinkSteps);
        Assert.AreEqual((int)CfgMgr32.CR_REMOVE_VETOED, sinkSteps[0].Code);
        Assert.AreEqual((int)CfgMgr32.CR_SUCCESS, sinkSteps[1].Code);
    }

    [TestMethod]
    public void AVetoThatSurvivesTheRetryIsPartialAndIsNotRetriedAgain()
    {
        using var h = new Harness();
        h.Nodes[SinkNode].DisableResult = CfgMgr32.CR_REMOVE_VETOED;
        int waits = 0;
        h.RetryWait = _ =>
        {
            waits++;
            return true;
        };

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual(1, waits, "One retry, never more.");
        Assert.HasCount(2, h.Nodes.Calls.Where(c => c.InstanceId == SinkNode));
        Assert.AreEqual(10, h.Disables);
    }

    // The block is never cut short, so a retry starts only when its delay and a block as long as the reserve still fit.
    [TestMethod]
    [DataRow(1_400, true)]
    [DataRow(1_600, false)]
    public void ARetryIsMadeOnlyWhenTheDelayAndTheReserveStillFit(int elapsedMilliseconds, bool retried)
    {
        using var h = new Harness();
        h.Nodes[SinkNode].DisableResult = CfgMgr32.CR_REMOVE_VETOED;
        int waits = 0;
        h.RetryWait = _ =>
        {
            waits++;
            return true;
        };
        h.Time.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));

        h.Run();

        Assert.AreEqual(retried ? 1 : 0, waits, elapsedMilliseconds + " ms gone of " + PreshutdownBudget.Total.TotalMilliseconds);
    }

    [TestMethod]
    public void TheLockWaitsAndTheReserveForTheBlockFitInsideTheBudget()
    {
        Assert.IsTrue(PreshutdownBudget.RunLockWait + PreshutdownBudget.BlockReserve <= PreshutdownBudget.Total);
        Assert.IsTrue(PreshutdownBudget.WaitBudget + PreshutdownBudget.BlockReserve <= PreshutdownBudget.Total);
        Assert.AreEqual(PreshutdownBudget.VetoRetryDelay + PreshutdownBudget.BlockReserve, PreshutdownBudget.VetoRetryRoom);
        Assert.AreEqual(2_500, PreshutdownBudget.WaitBudget.TotalMilliseconds);
    }

    // A run lock that is waited for the whole time it is allowed, then a block whose vetoed node costs what the sample says
    // (5,460 ms), must still end, status file written, before the time Windows waits. With the old waits (up to 5,000 ms for
    // the run lock) the same run ended past 10,000 ms.
    [TestMethod]
    public void TheLongestWaitFollowedByAVetoedBlockOfTheSampleLengthEndsInsideTheBudget()
    {
        using var h = new Harness();
        h.RunLock = new AdvancingRunLock(h.Time);
        var slow = new SlowNodes(h.Nodes, h.Time, SinkNode, TimeSpan.FromMilliseconds(5_460));
        h.Nodes[SinkNode].DisableResult = CfgMgr32.CR_REMOVE_VETOED;
        h.NodeApi = slow;

        PreshutdownResult result = h.Run();

        TimeSpan elapsed = h.Time.GetUtcNow() - h.Started;
        Assert.IsTrue(result.StatusWritten, "The status file is written.");
        Assert.IsTrue(elapsed <= PreshutdownBudget.Total, "Ended after " + elapsed.TotalMilliseconds + " ms of " + PreshutdownBudget.Total.TotalMilliseconds);
        Assert.IsTrue(elapsed + TimeSpan.FromSeconds(2) <= TimeSpan.FromMilliseconds(10_000), "Leaves the two seconds Windows gives after the budget.");
        Assert.AreEqual(GateExitCode.Partial, result.Outcome, "The vetoed node was not retried: no room was left for a second block.");
        Assert.IsNull(result.RetryAfter);
    }

    [TestMethod]
    public void NoRetryIsMadeWhenLessThanTwoSecondsRemain()
    {
        using var h = new Harness();
        h.Nodes[SinkNode].DisableResult = CfgMgr32.CR_REMOVE_VETOED;
        int waits = 0;
        h.RetryWait = _ =>
        {
            waits++;
            return true;
        };
        h.Time.Advance(TimeSpan.FromMilliseconds(6_500));

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual(0, waits);
        Assert.AreEqual(9, h.Disables);
        Assert.IsNull(result.RetryAfter);
    }

    [TestMethod]
    public void NoRetryIsMadeForAFailureThatIsNotAVeto()
    {
        using var h = new Harness();
        h.Nodes[SinkNode].DisableResult = CfgMgr32.CR_ACCESS_DENIED;
        int waits = 0;
        h.RetryWait = _ =>
        {
            waits++;
            return true;
        };

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        Assert.AreEqual(0, waits);
    }

    [TestMethod]
    public void TheBudgetIsEightSecondsAndTheStatusFileStartsWhenTheControlArrived()
    {
        Assert.AreEqual(8_000, PreshutdownBudget.Total.TotalMilliseconds);
        using var h = new Harness();
        DateTimeOffset control = h.Time.GetUtcNow();

        h.Run(deadline: control + PreshutdownBudget.Total);

        Assert.AreEqual(control, h.Status().StartedUtc);
    }

    // preshutdown is only the name of the service's status file. No command line, task request or Run of the gate can
    // start it, and nothing in this class ever enables a node.
    [TestMethod]
    public void TheGateRefusesPreshutdownAsAVerbAndNoRunEverEnablesANode()
    {
        using var h = new Harness();
        var actions = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, h.Time);

        GateExitCode rejected = actions.Run(new GateRequest(GateVerbs.Preshutdown, Nonce, null));

        Assert.AreEqual(GateExitCode.Rejected, rejected);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsFalse(Program.TryParseGateArgs(["gate", GateVerbs.Preshutdown, Nonce], out _, out _));
        Assert.IsFalse(Program.TryParseGateArgs(["gate-protect", GateVerbs.Preshutdown, Nonce], out _, out _));

        h.Run();
        h.BlockAll();
        h.Run();
        Assert.IsFalse(h.Nodes.Calls.Any(c => c.Kind == "enable"), "The shut-down block only moves nodes towards disabled.");
    }

    [TestMethod]
    public void AnUnexpectedFailureIsAStepAndTheStatusFileIsStillWritten()
    {
        using var h = new Harness();
        h.Nodes.ListResult = CfgMgr32.CR_SUCCESS;
        h.LockWait = _ => throw new InvalidOperationException("boom");
        using FileStream holder = new(Path.Combine(h.Machine, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

        PreshutdownResult result = h.Run();

        Assert.AreEqual(GateExitCode.Failed, result.Outcome);
        Assert.IsTrue(h.StatusFileExists);
        Assert.IsTrue(h.Status().Steps.Any(s => s.Step == "preshutdown-stopped" && !s.Ok));
        Assert.IsTrue(h.Log.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException));
    }

    [TestMethod]
    public void TheStatusFileIsPrunedToTheNewestSixteenWhenTheLockIsHeld()
    {
        using var h = new Harness();
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(h.Store.StatusFile(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), "{}");
        }

        h.BlockAll();
        h.Run();

        Assert.AreEqual(GateStore.MaxStatusFilesKept, Directory.GetFiles(h.Machine, "status-*.json").Length);
        Assert.IsTrue(h.StatusFileExists);
    }

    // The two settings share config.json, so writing one must keep the other.
    [TestMethod]
    [DataRow(GateVerbs.SetHandBackOn, true, false)]
    [DataRow(GateVerbs.SetHandBackOff, true, true)]
    [DataRow(GateVerbs.SetHandBackOn, false, false)]
    [DataRow(GateVerbs.SetBootOn, false, false)]
    [DataRow(GateVerbs.SetBootOff, true, true)]
    [DataRow(GateVerbs.SetBootOff, true, false)]
    public void EachSettingVerbKeepsTheOtherSetting(string verb, bool blockBefore, bool handBackBefore)
    {
        using var h = new Harness();
        Assert.IsTrue(h.Store.WriteConfig(new GateConfig { BlockAtBoot = blockBefore, HandBackAtShutdown = handBackBefore }).Ok);

        GateExitCode exit = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, h.Time).Run(new GateRequest(verb, Nonce, null));

        Assert.AreEqual(GateExitCode.Success, exit);
        GateConfig after = h.Store.ReadConfig().Value!;
        bool handBackExpected = verb is GateVerbs.SetHandBackOn ? true : verb is GateVerbs.SetHandBackOff ? false : handBackBefore;
        bool blockExpected = verb is GateVerbs.SetBootOn ? true : verb is GateVerbs.SetBootOff ? false : blockBefore;
        Assert.AreEqual(blockExpected, after.BlockAtBoot);
        Assert.AreEqual(handBackExpected, after.HandBackAtShutdown);
        Assert.IsEmpty(h.Nodes.Calls, "A setting verb changes no node.");
    }

    [TestMethod]
    public void TheHandBackSettingVerbsNeedAConfigAndNeverMakeOne()
    {
        using var h = new Harness(writeConfig: false);
        var actions = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, h.Time);

        Assert.AreEqual(GateExitCode.NoConfig, actions.Run(new GateRequest(GateVerbs.SetHandBackOn, Nonce, null)));
        Assert.AreEqual(GateExitCode.NoConfig, actions.Run(new GateRequest(GateVerbs.SetHandBackOff, Nonce, null)));

        Assert.IsFalse(File.Exists(h.Store.ConfigFile), "A request queued behind uninstall must not make a config.json.");
    }

    [TestMethod]
    public void TheHandBackSettingVerbsRunOnlyInGateMode()
    {
        using var h = new Harness();
        var actions = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, h.Time);

        Assert.AreEqual(GateExitCode.Rejected, actions.Run(new GateRequest(GateVerbs.SetHandBackOff, Nonce, null, GateMode.Protect)));
        Assert.IsTrue(h.Store.ReadConfig().Value!.HandBackAtShutdown);
    }

    [TestMethod]
    public void AHandBackSettingOverAnInvalidConfigWritesTheDefaultsAndRecordsTheRead()
    {
        using var h = new Harness(writeConfig: false);
        File.WriteAllText(h.Store.ConfigFile, "not json");

        GateExitCode exit = new GateActions(h.Nodes, h.Store, h.Folders, h.Log, h.Time).Run(new GateRequest(GateVerbs.SetHandBackOff, Nonce, null));

        Assert.AreEqual(GateExitCode.Success, exit);
        GateConfig after = h.Store.ReadConfig().Value!;
        Assert.IsTrue(after.BlockAtBoot);
        Assert.IsFalse(after.HandBackAtShutdown);
        Assert.IsTrue(h.Status().Steps.Any(s => s.Step == "read-config" && !s.Ok));
    }
}
