using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// The two fences between Play from a phone and the device Earshot manages: the rule that says which device that is,
// and safe mode, which refuses everything that would make the radio act.
[TestClass]
public sealed class StreamingExclusionTests
{
    private static readonly Guid Pinned = new("5C3A9E21-4B7D-5F18-9A6C-2D8E0B4F7A13");
    private static readonly Guid Other = new("7E2D4C8A-1B3F-5A6E-B9D0-6C4A2F8E1D35");

    [TestMethod]
    public void WithADevicePinnedThePinnedContainerIsTheRule()
    {
        Assert.IsTrue(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Anything", Pinned), Pinned, "AirPods"));
        Assert.IsFalse(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test Phone", Other), Pinned, "AirPods"));

        // A phone whose name happens to hold the match text is still a phone: with a container to go by, the name
        // decides nothing, so a match string like the owner's first name cannot hide the owner's phone.
        Assert.IsFalse(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test AirPods Phone", Other), Pinned, "AirPods"));
    }

    [TestMethod]
    public void WithNothingPinnedOrNoContainerReportedTheNameRuleIsAllThereIs()
    {
        Assert.IsTrue(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test airpods Pro", Other), Guid.Empty, "AirPods"));
        Assert.IsFalse(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test Phone", Other), Guid.Empty, "AirPods"));
        Assert.IsTrue(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test AirPods Pro", Guid.Empty), Pinned, "AirPods"));
        Assert.IsFalse(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test Phone", Guid.Empty), Pinned, "AirPods"));
        Assert.IsFalse(StreamingExclusion.IsManagedDevice(new StreamingDevice("id", "Test Phone", Guid.Empty), Guid.Empty, ""));
    }

    [TestMethod]
    public async Task SafeModeRefusesEnableAndOpenAndNeverCallsTheRealPlatform()
    {
        var inner = new FakeStreamingPlatform();
        var log = new CapturingLog();
        IStreamingPlatform safe = SafeStreamingPlatform.Wrap(inner, log);

        StreamingEnableOutcome enable = await safe.EnableAsync("phone", CancellationToken.None);
        StreamingOpenOutcome open = await safe.OpenAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingEnableStatus.StartFailed, enable.Status);
        Assert.AreEqual(StreamingOpenStatus.CallFailed, open.Status);
        Assert.AreEqual(NativeCodes.NotAttempted, enable.Step.Code);
        Assert.AreEqual(NativeCodes.NotAttempted, open.Step.Code);
        Assert.AreEqual(SafeDecorators.Message, enable.Step.Detail);
        Assert.IsEmpty(inner.Calls, "The wrapped platform must never be reached for a member safe mode refuses.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: play from a phone (enable)"));
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: play from a phone (open)"));
        Assert.AreSame(safe, SafeStreamingPlatform.Wrap(safe, log), "Wrapping twice adds nothing.");
    }

    [TestMethod]
    public async Task SafeModePassesTheReadsAndTheReleaseThrough()
    {
        var inner = new FakeStreamingPlatform { Support = StreamingSupport.TypeMissing };
        IStreamingPlatform safe = SafeStreamingPlatform.Wrap(inner, new CapturingLog());
        StreamingLinkChanged? seen = null;
        safe.LinkChanged += (_, e) => seen = e;

        Assert.AreEqual(StreamingSupport.TypeMissing, safe.CheckSupport().Support);
        await safe.ListStreamCapableDevicesAsync(CancellationToken.None);
        safe.Release("phone");
        inner.RaiseLink("phone", StreamingLinkState.Closed);

        CollectionAssert.AreEqual(Sequence.Of("List", "Release(phone)"), inner.Calls.ToArray());
        Assert.IsNotNull(seen);
    }

    // Through the coordinator, in safe mode, the owner is told it failed and nothing is left enabled.
    [TestMethod]
    public async Task InSafeModeAStartThroughTheCoordinatorReachesNoDevice()
    {
        var inner = new FakeStreamingPlatform();
        inner.NextDiscovery(FakeStreamingPlatform.Found(new StreamingDevice("phone", "Test Phone", Other)));
        using var coordinator = new StreamingCoordinator(
            SafeStreamingPlatform.Wrap(inner, new CapturingLog()), new ManualBusyGate(), StreamingSettings.Default, _ => false, new TestTimeProvider());
        await coordinator.RefreshAsync(CancellationToken.None);

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.NotEnabled, outcome.Status);
        Assert.IsEmpty(inner.CallsNamed("Enable("));
        Assert.IsEmpty(inner.CallsNamed("Open("));
    }
}
