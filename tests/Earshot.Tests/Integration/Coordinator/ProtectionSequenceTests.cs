using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Protection follows ProtectionPolicy: a service state changes only while the nodes are enabled, a request made
// while they are not is kept, and the microphone caveat is shown once, whatever applied it.
[TestClass]
public sealed class ProtectionSequenceTests
{
    private static readonly string[] ProtectOnThenBlock = ["protect-on", "block"];
    private static readonly string[] ConnectAllowConnectProtectOn = ["connect", "allow", "connect", "protect-on"];
    private static readonly bool[] OnOffOn = [true, false, true];
    private static readonly bool[] OffThenOn = [false, true];
    private static readonly bool[] OnOnly = [true];
    private static readonly bool[] OffOnly = [false];
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] ConnectingTryingConnected = ["Connecting", BlockCoordinator.TryingAnotherWayMessage, "Connected"];

    // settingOn is the saved Protect audio quality intent, which the automatic checks act on. A menu click
    // passes the new value to SetProtectionAsync itself, so the tests of a click leave the setting off.
    private static CoordinatorHarness Protecting(AudioProtectionState services = AudioProtectionState.NotProtected, bool settingOn = false)
    {
        var h = new CoordinatorHarness(protectAudio: settingOn);
        h.Protection.State = services;
        return h;
    }

    [TestMethod]
    public void TheMicrophoneNoticeIsShownOnceWhateverLeavesTheServicesProtected()
    {
        using CoordinatorHarness h = Protecting();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        // Already in that state counts: the microphone is off either way.
        h.Protection.OnApply = (protect, _) =>
        {
            h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
            return Task.FromResult(ControllerResult.Already(protect ? "Audio quality is already protected" : "Audio quality protection is already off"));
        };
        h.SetProtection(protect: true);

        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice));
        Assert.IsTrue(h.Settings.Current.ProtectAudioNoticeShown);

        h.SetProtection(protect: false);
        h.Protection.OnApply = null;
        h.SetProtection(protect: true);

        CollectionAssert.AreEqual(OnOffOn, h.Protection.Applies);
        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice), "The notice was shown again.");
    }

    [TestMethod]
    public void APartialResultShowsTheNoticeOnlyWhenTheServicesAreProtected()
    {
        using CoordinatorHarness h = Protecting();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Protection.OnApply = (_, _) =>
        {
            h.Protection.State = AudioProtectionState.Protected;
            return Task.FromResult(new ControllerResult(OpStatus.Partial, "Audio quality protected, but not every step worked. Try again.", []));
        };

        h.SetProtection(protect: true);

        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice));
    }

    [TestMethod]
    public void APartialResultThatLeavesHandsfreeOnShowsNoNotice()
    {
        using CoordinatorHarness h = Protecting();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Protection.OnApply = (_, _) =>
        {
            h.Protection.State = AudioProtectionState.Partial;
            return Task.FromResult(new ControllerResult(OpStatus.Partial, "Handsfree is off, but Headset is still on. Try again.", []));
        };

        ControllerResult result = h.SetProtection(protect: true);

        Assert.AreEqual(OpStatus.Partial, result.Status);
        Assert.AreEqual(0, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice));
        Assert.IsFalse(h.Settings.Current.ProtectAudioNoticeShown);
    }

    [TestMethod]
    public void AChangeAskedForWhileTheNodesAreBlockedIsKeptByTheGate()
    {
        using CoordinatorHarness h = Protecting();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Protection.OnApply = (_, _) => Task.FromResult(new ControllerResult(
            OpStatus.NotAttempted, "Saved. It applies when the AirPods are allowed.",
            [StepOutcomes.NotAttempted("protect-on", "A device node is disabled.")]));

        ControllerResult result = h.SetProtection(protect: true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual("Saved. It applies when the AirPods are allowed.", result.UserMessage);
        Assert.AreEqual(true, h.Coordinator.PendingProtect);
        Assert.AreEqual(0, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice));
    }

    [TestMethod]
    public void AChangeAskedForWhileTheNodeStateIsUnknownIsKeptHere()
    {
        using CoordinatorHarness h = Protecting();
        h.Block.StatusFailure = new IOException("no status");
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        ControllerResult result = h.SetProtection(protect: true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(BlockCoordinator.SavedForNextConnectMessage, result.UserMessage);
        Assert.AreEqual(true, h.Coordinator.PendingProtect);
        Assert.IsEmpty(h.Protection.Applies, "Nothing was asked of the gate while the nodes were unreadable.");
    }

    [TestMethod]
    public void ARestoreKeptWhileTheNodeStateWasUnknownIsAppliedAfterARestart()
    {
        // Earshot turned Handsfree off earlier. The user turns protection off while the nodes cannot be read: the
        // tray saves the setting, and the request is kept in memory only.
        using (CoordinatorHarness before = Protecting(AudioProtectionState.Protected))
        {
            before.Block.StatusFailure = new IOException("no status");
            before.Monitor.Set(Devices.Idle(1));
            before.Start();
            ControllerResult kept = before.SetProtection(protect: false);
            Assert.AreEqual(BlockCoordinator.SavedForNextConnectMessage, kept.UserMessage);
            Assert.IsEmpty(before.Protection.Applies);
        }

        // Earshot restarts with the saved setting off and the services still protected.
        using CoordinatorHarness h = Protecting(AudioProtectionState.Protected, settingOn: false);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        CollectionAssert.AreEqual(OffOnly, h.Protection.Applies, "The restore the card promised was lost at the restart.");
        Assert.AreEqual(AudioProtectionState.NotProtected, h.Protection.State);
    }

    [TestMethod]
    public void ARestoreTheControllerDoesNotTakeIsNotAskedForAgainInTheSameRun()
    {
        using CoordinatorHarness h = Protecting(AudioProtectionState.Protected, settingOn: false);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));

        // Handsfree was turned off outside Earshot, so the controller leaves it off.
        h.Protection.OnApply = (_, _) => Task.FromResult(new ControllerResult(OpStatus.NotAttempted,
            "Handsfree was turned off outside Earshot, so it stays off.", [StepOutcomes.NotAttempted("protect-off", "protection.json lists no service Earshot turned off.")]));
        h.Start();
        CollectionAssert.AreEqual(OffOnly, h.Protection.Applies);

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(OffOnly, h.Protection.Applies, "The restore was asked for again at every connect.");
        CollectionAssert.DoesNotContain(h.Cards.Statuses, "Handsfree was turned off outside Earshot, so it stays off.");
    }

    [TestMethod]
    public void TheAllowSequenceAppliesTheRequestThatWasKept()
    {
        using CoordinatorHarness h = Protecting(settingOn: true);
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Protection.Pending = true;
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(ConnectAllowConnectProtectOn, h.Trace);
        Assert.AreEqual(AudioProtectionState.Protected, h.Protection.State);
        Assert.IsNull(h.Coordinator.PendingProtect);
    }

    [TestMethod]
    public void ProtectionGoesOffBeforeTheNodesAreBlocked()
    {
        using CoordinatorHarness h = Protecting(AudioProtectionState.Protected, settingOn: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        Assert.IsEmpty(h.Trace, "Protection was already on, so the start-up check changed nothing.");

        // Windows turned Handsfree back on while they were in use.
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(ProtectOnThenBlock, h.Trace, "Handsfree must be turned off while the nodes are still enabled.");
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
    }

    [TestMethod]
    public void TheCheckAfterAConnectPutsProtectionBack()
    {
        using CoordinatorHarness h = Protecting(AudioProtectionState.Protected, settingOn: true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        // Connecting brings Handsfree back, as the research says it can.
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Protection.State = AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Active(5));
            return Task.FromResult(Results.Connected());
        });

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(OnOnly, h.Protection.Applies);
        Assert.AreEqual(AudioProtectionState.Protected, h.Protection.State);
        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice));
    }

    [TestMethod]
    public void AConnectWhoseProtectionCheckFailsIsPartialAndKeepsTheRequest()
    {
        using CoordinatorHarness h = Protecting(AudioProtectionState.Protected, settingOn: true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Protection.State = AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Active(5));
            return Task.FromResult(Results.Connected());
        });
        h.Protection.OnApply = (_, _) => Task.FromResult(ControllerResult.Fail("Could not protect audio quality. Try again.",
            [StepOutcomes.FromWin32("bluetooth-set-service-state:111E", 5)]));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Partial, report.Status);
        Assert.AreEqual(BlockCoordinator.ConnectedNotProtectedMessage, report.UserMessage);
        Assert.AreEqual(true, h.Coordinator.PendingProtect);
        Assert.AreEqual(BlockCoordinator.ConnectedNotProtectedMessage, h.Cards.Statuses[^1]);
    }

    [TestMethod]
    public void TheHandsFreeAssistedWayConnectsAndPutsProtectionBack()
    {
        using CoordinatorHarness h = HandsFreeCase();

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(OffThenOn, h.Protection.Applies);
        CollectionAssert.AreEqual(ConnectingTryingConnected, h.Cards.Statuses);
        Assert.AreEqual(AudioProtectionState.Protected, h.Protection.State);
        Assert.IsNull(h.Coordinator.PendingProtect);
        Assert.AreEqual(0, h.Cards.Statuses.Count(s => s == TrayStatus.MicrophoneNotice), "The notice is never shown again for this.");
    }

    [TestMethod]
    public void TheHandsFreeAssistedWayPutsProtectionBackAndBlocksWhenTheConnectStillFails()
    {
        using CoordinatorHarness h = HandsFreeCase();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.CouldNotReachDriverMessage, report.UserMessage);
        CollectionAssert.AreEqual(OffThenOn, h.Protection.Applies);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void TheHandsFreeAssistedWayReportsPartialWhenProtectionCannotBePutBack()
    {
        using CoordinatorHarness h = HandsFreeCase();
        h.Protection.OnApply = (protect, _) =>
        {
            if (protect)
            {
                return Task.FromResult(ControllerResult.Fail("Could not protect audio quality. Try again.",
                    [StepOutcomes.FromWin32("bluetooth-set-service-state:111E", 5)]));
            }

            h.Protection.State = AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(20));
            return Task.FromResult(ControllerResult.Ok("Audio quality protection is off"));
        };

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Partial, report.Status);
        Assert.AreEqual(BlockCoordinator.ConnectedNotProtectedMessage, report.UserMessage);
        Assert.AreEqual(true, h.Coordinator.PendingProtect);
    }

    [TestMethod]
    public void NothingIsTriedAnotherWayWhenNothingReachedTheA2dpFilter()
    {
        using CoordinatorHarness h = HandsFreeCase();
        h.Connection.Connects.Clear();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NothingSent()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsEmpty(h.Protection.Applies, "Protection was turned off for a filter that was never asked.");
        CollectionAssert.DoesNotContain(h.Cards.Statuses, BlockCoordinator.TryingAnotherWayMessage);
    }

    [TestMethod]
    public void NothingIsTriedAnotherWayWhenTheDeviceCannotBeRead()
    {
        using CoordinatorHarness h = HandsFreeCase();

        // The endpoints cannot be enumerated, so nothing shows render is not ACTIVE.
        h.Publish(Devices.Unreadable(40));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsEmpty(h.Protection.Applies, "Protection was turned off without a good read of the device.");
        CollectionAssert.DoesNotContain(h.Cards.Statuses, BlockCoordinator.TryingAnotherWayMessage);
    }

    [TestMethod]
    public void TheHandsFreeAssistedWayKeepsARestoreTheUserAskedFor()
    {
        using CoordinatorHarness h = HandsFreeCase();

        // The user turned protection off while the AirPods were blocked, and the gate kept the restore.
        h.Settings.Update(s => s.ProtectAudioQuality = false);
        h.Protection.Pending = false;

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(OffOnly, h.Protection.Applies, "Protection was put back on against the saved setting.");
        Assert.AreEqual(AudioProtectionState.NotProtected, h.Protection.State);
        Assert.IsFalse(h.Settings.Current.ProtectAudioQuality);
        Assert.IsNull(h.Coordinator.PendingProtect);
    }

    [TestMethod]
    public void TheHandsFreeAssistedWayLeavesProtectionOffAfterAFailureWhenTheSettingIsOff()
    {
        using CoordinatorHarness h = HandsFreeCase();
        h.Settings.Update(s => s.ProtectAudioQuality = false);
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(OffOnly, h.Protection.Applies);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The nodes are blocked again all the same.");
        h.AssertAtRest();
    }

    [TestMethod]
    public void AMicrophoneNoticeHeldBackByWindowsFollowsTheNextClick()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));

        // A full-screen app or quiet time: the start-up check applies protection, but its notice is held back.
        h.Cards.HoldBackNearTray = true;
        h.Start();

        CollectionAssert.AreEqual(OnOnly, h.Protection.Applies);
        Assert.AreEqual(1, h.Cards.HeldBack.Count(c => c.Content.Status == TrayStatus.MicrophoneNotice));
        Assert.IsFalse(h.Settings.Current.ProtectAudioNoticeShown, "A notice nobody saw was remembered as shown.");

        h.Cards.HoldBackNearTray = false;
        var click = new System.Drawing.Point(1830, 1040);
        h.Toggle(connect: true, click: click);

        CardShown notice = h.Cards.Shown.Single(c => c.Content.Status == TrayStatus.MicrophoneNotice);
        Assert.AreEqual(CardAnchor.NearCursor, notice.Anchor);
        Assert.AreEqual(click, notice.ClickPoint);
        Assert.IsTrue(h.Settings.Current.ProtectAudioNoticeShown);

        h.Toggle(connect: false);
        h.Toggle(connect: true, click: click);
        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == TrayStatus.MicrophoneNotice), "The notice was shown again.");
    }

    // Protection is on, so the Hands-Free filter is gone, and the A2DP filter turns the request down.
    private static CoordinatorHarness HandsFreeCase()
    {
        var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Block.Status = Statuses.Allowed(blockAtBoot: true);
        h.Monitor.Set(Devices.Idle(1, capture: false));
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Start();

        // The start-up check blocks idle nodes; the nodes come back (as after an allow), which the monitor reports.
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Idle(10, capture: false));
        h.Cards.Shown.Clear();
        h.Block.Calls.Clear();
        h.Trace.Clear();
        h.Protection.Applies.Clear();

        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));
        h.Protection.Effect = protect =>
        {
            h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(protect ? 30 : 20, capture: !protect));
        };
        return h;
    }
}
