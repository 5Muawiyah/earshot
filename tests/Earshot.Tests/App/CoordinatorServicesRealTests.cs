using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using FakeSettings = Earshot.Tests.Integration.Coordinator.FakeSettings;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The real helpers behind the coordinator's link, session and settings services, over fakes except for one harmless read of the
// session's lock state.
[TestClass]
public sealed class CoordinatorServicesRealTests
{
    private static NodeLinkProbe Probe(FakeNodeApi table, out FakeSettings settings)
    {
        settings = new FakeSettings();
        settings.Update(s =>
        {
            s.PinnedContainerId = RecordedNodes.AirPodsContainer;
            s.PinnedAddress = RecordedNodes.AirPodsAddress;
        });
        return new NodeLinkProbe(table, settings, new CapturingLog());
    }

    [TestMethod]
    public void ADeviceNodeWithTheLinkBitClearIsConnected()
    {
        FakeNodeApi table = RecordedNodes.Table();

        Assert.AreEqual(LinkState.Connected, Probe(table, out _).Read());
    }

    [TestMethod]
    public void ADeviceNodeWithTheBitSetADisabledOneAndAnAbsentOneAreNotConnected()
    {
        FakeNodeApi table = RecordedNodes.Table();
        FakeNode device = table[RecordedNodes.AirPodsDeviceNode];

        device.Status |= CfgMgr32.DN_DEVICE_DISCONNECTED;
        Assert.AreEqual(LinkState.Disconnected, Probe(table, out _).Read());

        device.Status &= ~CfgMgr32.DN_DEVICE_DISCONNECTED;
        device.MarkDisabled(persistent: true);
        Assert.AreEqual(LinkState.Disconnected, Probe(table, out _).Read(), "A disabled node has no link, whatever its bit says.");

        device.Present = false;
        Assert.AreEqual(LinkState.Disconnected, Probe(table, out _).Read());
    }

    [TestMethod]
    public void AStatusThatCannotBeReadOrNothingPinnedIsUnknownAndChangesNothing()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table[RecordedNodes.AirPodsDeviceNode].StatusReadResult = CfgMgr32.CR_FAILURE;
        NodeLinkProbe probe = Probe(table, out FakeSettings settings);

        Assert.AreEqual(LinkState.Unknown, probe.Read());

        settings.Update(s => s.PinnedAddress = "");
        Assert.AreEqual(LinkState.Unknown, probe.Read());
        Assert.IsEmpty(table.Calls, "Reading the link changes nothing.");
    }

    // The one real execution: WTSQuerySessionInformation with WTSSessionInfoEx for the current session. A read only.
    [TestMethod]
    public void TheRealSessionLockStateReadsWithoutAnError()
    {
        WtsSession.Reading read = WtsSession.Read();

        Assert.AreEqual(string.Empty, read.Step, "The read stopped at " + read.Step + " with code " + read.Code);
        Assert.IsNotNull(read.Locked, "SessionFlags was " + read.Flags);
    }

    [TestMethod]
    public void TheSettableHintsReadUnknownUntilASourceIsGiven()
    {
        var hints = new SettableDeviceHints();
        Assert.AreEqual(DeviceHint.Unknown, hints.Read());
        hints.Source = () => DeviceHint.LidClosed;
        Assert.AreEqual(DeviceHint.LidClosed, hints.Read());
    }
}
