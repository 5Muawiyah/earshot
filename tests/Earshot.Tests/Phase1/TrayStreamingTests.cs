using System.Diagnostics;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Streaming;
using Earshot.Tests.Streaming;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The tray's Play from a phone wiring. Off by default with nothing built, read or offered; refused while the tray is
// busy with the AirPods, in safe mode and while a session ends; let go of on every way out, within a limit; and never
// a route to the block controller, the connection controller or the device Earshot manages. TrayHarness injects a
// FakeStreamingPlatform (TrayStartOptions.StreamingPlatformFactory), so nothing here touches WinRT or a real device.
[TestClass]
public sealed class TrayStreamingTests
{
    private const string PhoneId = StreamingCoordinatorTests.PlaceholderId;
    private const string PhoneName = "Test Phone";
    private const string MayStill = "Test Phone may still be connected to this PC. See the log.";

    private static readonly Guid PhoneContainer = IPhoneContainer;

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static FakeStreamingPlatform PlatformWith(params StreamingDevice[] devices)
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(devices));
        return fake;
    }

    private static StreamingDevice Phone(string id = PhoneId, string name = PhoneName) => new(id, name, PhoneContainer);

    private static TrayHarness On(FakeStreamingPlatform fake, bool safeMode = false, DeviceSnapshot? snapshot = null, TimeSpan? shutdownWait = null) =>
        new(safeMode: safeMode, snapshot: snapshot, settings: s => s.Streaming = s.Streaming with { Enabled = true }, streamingPlatform: fake, streamingShutdownWait: shutdownWait);

    private static ToolStripMenuItem Parent(TrayHarness tray)
    {
        tray.Context.Menu.Refresh();
        return tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PlayFromPhone);
    }

    private static void ClickChild(TrayHarness tray, string text)
    {
        tray.Context.Menu.Refresh();
        ToolStripMenuItem item = tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == text);
        Assert.IsTrue(item.Enabled, text + " cannot be clicked.");
        item.PerformClick();
        tray.PumpUntilIdle();
    }

    [TestMethod]
    public void OffByDefaultNothingIsBuiltReadOrOffered()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();

            // Every trigger the feature has, pulled while it is off.
            tray.Context.OnIconMouseDownForStreaming(null, Press(MouseButtons.Right));
            tray.Context.OnPlayFromPhoneItemClicked(null, new StreamingMenuItemEventArgs(new StreamingMenuItem(PhoneName, true, false, PhoneId, StreamingMenuCommand.Play)));
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.Streaming.Enabled, "Defaults must be off.");
            Assert.AreEqual(0, tray.StreamingPlatformsBuilt, "No platform is built while the feature is off.");
            Assert.AreEqual(0, tray.Streaming.CheckSupportCalls);
            Assert.IsEmpty(tray.Streaming.Calls, "Nothing is enumerated, enabled or opened while the feature is off.");
            Assert.IsFalse(Parent(tray).Available, "The menu item is not there at all while the feature is off.");
            Assert.AreEqual(0, tray.Context.Menu.PlayFromPhoneItems.Count);
            Assert.AreEqual(TrayStatus.Tooltip(tray.Monitor.Current, tray.Coordinator.BlockStatus, tray.Settings.Current), tray.Context.TooltipText);
        });
    }

    [TestMethod]
    public void SwitchingOnChecksSupportOnceReadsTheListAndOffersTheDevices()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone(), new StreamingDevice("console", "Console", Guid.NewGuid()));
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.StreamingPlatformsBuilt);
            Assert.AreEqual(1, fake.CheckSupportCalls);
            CollectionAssert.AreEqual(Sequence.Of("List"), fake.Calls.ToArray(), "Switching on reads the list and does nothing else: nothing is enabled or opened.");

            ToolStripMenuItem parent = Parent(tray);
            Assert.IsTrue(parent.Available && parent.Enabled);
            CollectionAssert.AreEqual(
                Sequence.Of(PhoneName, "Console", "Refresh the list"),
                tray.Context.Menu.PlayFromPhoneItems.Select(i => i.Text).ToArray());

            // Opening the menu again reads nothing: the items come from cached state.
            tray.Context.Menu.Refresh();
            tray.Context.Menu.Refresh();
            tray.PumpUntilIdle();
            Assert.AreEqual(1, fake.CallsNamed("List").Count);
        });
    }

    [TestMethod]
    public void TheMenuItemSitsUnderTheToggleAndTheRestOfTheMenuIsUnchanged()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = On(PlatformWith(Phone()));
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();

            string[] texts = tray.Context.Menu.Items.Where(i => i.Available).Select(i => i is ToolStripSeparator ? "-" : i.Text ?? "").ToArray();

            CollectionAssert.AreEqual(
                Sequence.Of(
                    "Connect", "Play from a phone", "-",
                    "Block at boot", "Protect audio quality", "Turns off the AirPods microphone", "Open on startup",
                    "Speak status", "-",
                    "Choose device...", "-",
                    "Exit"),
                texts);
        });
    }

    [TestMethod]
    public void ClickingADeviceEnablesThenOpensAnswersOnACardAndSavesNothingAboutIt()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            string settingsBefore = File.ReadAllText(tray.SettingsPath);

            ClickChild(tray, PhoneName);

            CollectionAssert.AreEqual(Sequence.Of("List", "Enable(" + PhoneId + ")", "Open(" + PhoneId + ")"), fake.Calls.ToArray());
            Assert.AreEqual("Waiting for Test Phone. Start playing something on it.", tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(1, tray.StreamingPlatformsBuilt);
            Assert.IsEmpty(fake.CallsNamed("Release("));

            // Nothing about the device is saved, not even a hash of its id: the settings file is what it was.
            Assert.AreEqual(settingsBefore, File.ReadAllText(tray.SettingsPath));
            Assert.IsFalse(File.ReadAllText(tray.SettingsPath).Contains(StreamingLog.Key(PhoneId), StringComparison.OrdinalIgnoreCase));

            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == PhoneName).Checked);
            Assert.IsTrue(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Text == "Stop playing from Test Phone"));

            // The id Windows gave is never written anywhere: not to the settings file, not to the log, not to a card.
            string json = File.ReadAllText(tray.SettingsPath);
            foreach (string part in Sequence.Of(PhoneId, "BTHENUM", "PLACEHOLDER"))
            {
                Assert.IsFalse(json.Contains(part, StringComparison.OrdinalIgnoreCase), "settings.json carries part of the device id.");
                Assert.IsFalse(tray.Log.Entries.Any(e => e.Message.Contains(part, StringComparison.OrdinalIgnoreCase)), "The log carries part of the device id.");
                Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status.Contains(part, StringComparison.OrdinalIgnoreCase) || c.Content.Title.Contains(part, StringComparison.OrdinalIgnoreCase)));
            }

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "device " + StreamingLog.Key(PhoneId) + ": Open"), "The log names the device by its hash.");
        });
    }

    [TestMethod]
    public void TheTooltipCarriesTheLineOnlyWhileADeviceIsInUse()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake, snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            string atRest = TrayStatus.Tooltip(tray.Monitor.Current, tray.Coordinator.BlockStatus, tray.Settings.Current);
            Assert.AreEqual(atRest, tray.Context.TooltipText);

            ClickChild(tray, PhoneName);
            Assert.AreEqual(atRest + "\nWaiting for Test Phone. Start playing something on it.", tray.Context.TooltipText);

            // A link change arrives on a thread of Windows' own; the tray gets itself back onto its UI thread.
            Task.Run(() => fake.RaiseLink(PhoneId, StreamingLinkState.Opened)).Wait(TimeSpan.FromSeconds(10));
            TrayHarness.PumpUntil(() => tray.Context.TooltipText.EndsWith("is connected and can play through this PC.", StringComparison.Ordinal), "The tooltip never took up the link change.");
            Assert.IsTrue(tray.Context.TooltipText.Length <= TrayStatus.MaxTooltipLength);

            ClickChild(tray, "Stop playing from Test Phone");
            Assert.AreEqual(atRest, tray.Context.TooltipText, "With nothing in use the tooltip is what it always was.");
            Assert.AreEqual("This PC is no longer accepting audio from Test Phone.", tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual("Release(" + PhoneId + ")", fake.Calls[^1]);
        });
    }

    [TestMethod]
    public void AFailureGetsOneShortCardTheCodeGoesToTheLogAndNothingStaysEnabled()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            fake.OpenAnswers(PhoneId, StreamingOpenStatus.UnknownFailure, unchecked((int)0x8007048F));
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            int cardsBefore = tray.Cards.Shown.Count;

            ClickChild(tray, PhoneName);

            Assert.AreEqual(cardsBefore + 1, tray.Cards.Shown.Count, "One card, not one per step.");
            Assert.AreEqual("Could not connect to Test Phone. See the log.", tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "UnknownFailure"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "0x8007048F"), "The raw code Windows gave is recorded.");
            Assert.AreEqual("Release(" + PhoneId + ")", fake.Calls[^1]);

            // And the tray carries on: the next click works.
            fake.OpenAnswers(PhoneId, StreamingOpenStatus.Open);
            ClickChild(tray, PhoneName);
            Assert.AreEqual("Waiting for Test Phone. Start playing something on it.", tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void WhileTheTrayIsBusyWithTheAirPodsAStartIsRefusedAndTheConnectIsNeverHeldUp()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake, snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            var connect = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Connection.OnConnect = _ => connect.Task;

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            TrayHarness.PumpUntil(() => tray.Connection.Calls.Count == 1, "The connect never started.");
            tray.Context.Menu.Refresh();
            tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == PhoneName).PerformClick();
            TrayHarness.PumpUntil(() => tray.Cards.Shown.Any(c => c.Content.Status == "Busy just now. Try again in a moment."), "The refusal was never shown.");

            Assert.IsEmpty(fake.CallsNamed("Enable("), "Nothing reaches Windows while a connect is in flight.");
            Assert.IsEmpty(fake.CallsNamed("Open("));

            connect.SetResult(new ConnectResult(ConnectOutcome.Confirmed, "Connected", []));
            tray.PumpUntilIdle();

            // The other way round: a start in flight never holds up the owner's connect or disconnect.
            var answer = new TaskCompletionSource<StreamingOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.OpenRuns(PhoneId, _ => answer.Task);
            tray.Context.Menu.Refresh();
            tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == PhoneName).PerformClick();
            Assert.IsTrue(fake.OpenEntered(PhoneId).Wait(TimeSpan.FromSeconds(10)), "The open never started.");
            Assert.IsFalse(tray.Context.IsBusy, "A streaming start is not a reason to refuse a connect.");
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(MenuModel.Connect).Enabled, "The AirPods toggle stays clickable while a streaming start is in flight.");

            // A second click while that start is in flight is answered, not queued behind it.
            tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == PhoneName).PerformClick();
            Assert.AreEqual("Busy just now. Try again in a moment.", tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(1, fake.CallsNamed("Enable(").Count);

            answer.SetResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, PhoneId, StepOutcomes.FromHResult("streaming-open", 0)));
            tray.PumpUntilIdle();
        });
    }

    [TestMethod]
    public void WhileASessionEndIsInProgressNothingNewIsStarted()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            int listsBefore = fake.CallsNamed("List").Count;

            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Coordinator.SessionEndInProgress);

            ClickChild(tray, PhoneName);
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tray.Cards.Shown[^1].Content.Status);
            ClickChild(tray, "Refresh the list");
            tray.Context.OnIconMouseDownForStreaming(null, Press(MouseButtons.Right));
            tray.PumpUntilIdle();

            Assert.IsEmpty(fake.CallsNamed("Enable("));
            Assert.IsEmpty(fake.CallsNamed("Open("));
            Assert.AreEqual(listsBefore, fake.CallsNamed("List").Count, "Not even the list is read while the session ends.");

            // Cancelled: the tray is usable again at once.
            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
            ClickChild(tray, PhoneName);
            Assert.AreEqual("Open(" + PhoneId + ")", fake.Calls[^1]);
        });
    }

    [TestMethod]
    public void SwitchingOnWhileASessionEndIsInProgressBuildsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness();
            tray.PumpUntilIdle();
            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));

            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(0, tray.StreamingPlatformsBuilt);
            Assert.IsEmpty(tray.Streaming.Calls);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Play from a phone: not started, because the session is ending."));
        });
    }

    [TestMethod]
    public void WhenTheSessionReallyEndsTheConnectionIsLetGoWithoutWaiting()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);

            // A query alone lets go of nothing: the session end may still be cancelled, and the music with it.
            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
            tray.PumpUntilIdle();
            Assert.IsEmpty(fake.CallsNamed("Release("));

            tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
            TrayHarness.PumpUntil(() => fake.CallsNamed("Release(").Count == 1, "The connection was not let go when the session ended.");

            Assert.AreEqual("Release(" + PhoneId + ")", fake.Calls[^1]);
            Assert.IsFalse(Parent(tray).Available);
        });
    }

    [TestMethod]
    public void ClosingLetsGoOfEveryConnectionOnEveryWayOut()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);

            tray.Context.Dispose();

            Assert.AreEqual(1, fake.CallsNamed("Release(").Count, "Close must release and dispose the connection.");
            Assert.IsFalse(fake.HasLinkSubscribers, "The coordinator lets go of the platform's event too.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "released device " + StreamingLog.Key(PhoneId)));
        });
    }

    [TestMethod]
    public void ExitFromTheMenuCancelsAnOpenInFlightAndStillLetsGo()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            fake.OpenNeverAnswers(PhoneId);
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();
            tray.Context.Menu.PlayFromPhoneItems.Single(i => i.Text == PhoneName).PerformClick();
            Assert.IsTrue(fake.OpenEntered(PhoneId).Wait(TimeSpan.FromSeconds(10)), "The open never started.");
            var watch = Stopwatch.StartNew();

            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);
            Application.Run(tray.Context);
            watch.Stop();

            // An open Windows never answers cannot keep Earshot alive: Exit cancels it, it lets go of what it enabled,
            // and the message loop ends long before the open's own limit, let alone Exit's.
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), "Exit took " + watch.Elapsed);
            Assert.AreEqual("Release(" + PhoneId + ")", fake.Calls[^1]);
            Assert.AreEqual(1, fake.CallsNamed("Release(").Count);
        });
    }

    [TestMethod]
    public void ClosingIsBoundedEvenWhenWindowsNeverLetsGo()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            var held = new ManualResetEventSlim(false);
            using TrayHarness tray = On(fake, shutdownWait: TimeSpan.FromMilliseconds(200));
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);
            fake.ReleaseHeldUntil = held;

            var watch = Stopwatch.StartNew();
            tray.Context.Dispose();
            watch.Stop();

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), "Close was held for " + watch.Elapsed + " by a release that never returned.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Windows had not let go of the connection"), "Giving up the wait is recorded, not silent.");

            // Let the pool thread go, so nothing is left running. The event is not disposed: that thread may still be
            // on its way out of Wait.
            held.Set();
        });
    }

    [TestMethod]
    public void SwitchingOffLetsGoAndTakesTheMenuItemAway()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);

            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = false });
            TrayHarness.PumpUntil(() => fake.CallsNamed("Release(").Count == 1, "Switching off did not let go of the connection.");
            tray.PumpUntilIdle();

            Assert.IsFalse(Parent(tray).Available);
            Assert.IsFalse(fake.HasLinkSubscribers);
        });
    }

    [TestMethod]
    public void SafeModeRefusesAStartAndNothingReachesThePlatform()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake, safeMode: true);
            tray.PumpUntilIdle();

            ClickChild(tray, PhoneName);

            // One fence, at the bottom: the platform the tray built is wrapped, so the click goes all the way down and
            // is refused there. The list was still read, which safe mode allows: it changes nothing.
            Assert.AreEqual(SafeDecorators.Message, tray.Cards.Shown[^1].Content.Status);
            CollectionAssert.AreEqual(Sequence.Of("List"), fake.Calls.Where(c => !c.StartsWith("Release(", StringComparison.Ordinal)).ToArray());
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Refused: play from a phone (enable)"));
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Checked), "Nothing is in use after a refusal.");
        });
    }

    [TestMethod]
    public void TheDeviceEarshotManagesIsNeverOffered()
    {
        StaThread.Run(() =>
        {
            // The harness pins AirPodsContainer. A device in that container is the managed one, whatever it is called.
            FakeStreamingPlatform fake = PlatformWith(Phone(), new StreamingDevice("managed", "Something Else", AirPodsContainer));
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();

            CollectionAssert.AreEqual(
                Sequence.Of(PhoneName, "Refresh the list"),
                tray.Context.Menu.PlayFromPhoneItems.Select(i => i.Text).ToArray());

            // Even a click forged for it reaches nothing.
            tray.Context.OnPlayFromPhoneItemClicked(null, new StreamingMenuItemEventArgs(new StreamingMenuItem("Something Else", true, false, "managed", StreamingMenuCommand.Play)));
            tray.PumpUntilIdle();
            Assert.IsEmpty(fake.CallsNamed("Enable("));
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "is the one Earshot manages"));
        });
    }

    [TestMethod]
    public void AWholeSessionOfPlayingNeverTouchesTheBlockControllerOrTheConnection()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake, snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            string[] blockBefore = tray.Block.Calls.ToArray();
            int connectBefore = tray.Connection.Calls.Count;
            int protectBefore = tray.Protection.Calls.Count;
            int runValueWrites = tray.Startup.Writes;

            ClickChild(tray, "Refresh the list");
            ClickChild(tray, PhoneName);
            Task.Run(() => fake.RaiseLink(PhoneId, StreamingLinkState.Opened)).Wait(TimeSpan.FromSeconds(10));
            Task.Run(() => fake.RaiseLink(PhoneId, StreamingLinkState.Closed)).Wait(TimeSpan.FromSeconds(10));
            tray.PumpUntilIdle();
            ClickChild(tray, "Stop playing from Test Phone");

            CollectionAssert.AreEqual(blockBefore, tray.Block.Calls.ToArray(), "No allow, no block, no setup, no boot setting: nothing at all.");
            Assert.IsFalse(tray.Block.Calls.Contains("allow"));
            Assert.AreEqual(connectBefore, tray.Connection.Calls.Count);
            Assert.AreEqual(protectBefore, tray.Protection.Calls.Count);
            Assert.AreEqual(runValueWrites, tray.Startup.Writes);
        });
    }

    [TestMethod]
    public void AnUnsupportedWindowsShowsOneCardAndADisabledItemAndReadsNothing()
    {
        StaThread.Run(() =>
        {
            var fake = new FakeStreamingPlatform
            {
                Support = StreamingSupport.TypeMissing,
                SupportStep = new StepOutcome("streaming-support", false, unchecked((int)0x80040154), "REGDB_E_CLASSNOTREG", "COMException"),
            };
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();

            Assert.AreEqual("This copy of Windows cannot receive Bluetooth audio.", tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == "This copy of Windows cannot receive Bluetooth audio."));
            ToolStripMenuItem parent = Parent(tray);
            Assert.IsTrue(parent.Available);
            Assert.IsFalse(parent.Enabled);
            Assert.IsEmpty(fake.Calls);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "REGDB_E_CLASSNOTREG"), "The code behind the refusal is recorded.");

            tray.Context.OnIconMouseDownForStreaming(null, Press(MouseButtons.Right));
            tray.PumpUntilIdle();
            Assert.IsEmpty(fake.Calls);
        });
    }

    [TestMethod]
    public void TheRightButtonReadsTheListAgainAndTheLeftButtonDoesNot()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            Assert.AreEqual(1, fake.CallsNamed("List").Count);

            tray.Context.OnIconMouseDownForStreaming(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();
            Assert.AreEqual(1, fake.CallsNamed("List").Count);

            tray.Context.OnIconMouseDownForStreaming(null, Press(MouseButtons.Right));
            tray.PumpUntilIdle();
            Assert.AreEqual(2, fake.CallsNamed("List").Count);
        });
    }

    [TestMethod]
    public void AFailedReadOfTheListTheOwnerAskedForGetsACardAndTheNextReadStillRuns()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            fake.NextDiscovery(FakeStreamingPlatform.ListFailed(unchecked((int)0x80070005)));
            fake.NextDiscovery(FakeStreamingPlatform.Found(Phone()));

            ClickChild(tray, "Refresh the list");

            Assert.AreEqual("Could not read the list of paired devices.", tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "E_ACCESSDENIED"));
            tray.Context.Menu.Refresh();
            CollectionAssert.AreEqual(
                Sequence.Of("Could not read the list of paired devices.", "Refresh the list"),
                tray.Context.Menu.PlayFromPhoneItems.Select(i => i.Text).ToArray());
            Assert.IsFalse(tray.Context.Menu.PlayFromPhoneItems[0].Enabled, "A sentence is never clickable.");

            ClickChild(tray, "Refresh the list");
            tray.Context.Menu.Refresh();
            Assert.AreEqual(PhoneName, tray.Context.Menu.PlayFromPhoneItems[0].Text);
        });
    }

    // Item by item from the review. A release Windows does not confirm: one card that says the phone may still be
    // connected, the raw code in the log, and a menu that still offers to let go rather than claiming it happened.
    [TestMethod]
    public void AReleaseThatFailsGetsOneCardTheCodeInTheLogAndTheStopItemStays()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            fake.OpenAnswers(PhoneId, StreamingOpenStatus.TimedOut);
            fake.ReleaseFails(PhoneId);
            using TrayHarness tray = On(fake, snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            int cardsBefore = tray.Cards.Shown.Count;

            ClickChild(tray, PhoneName);

            Assert.AreEqual(cardsBefore + 1, tray.Cards.Shown.Count, "Told once.");
            Assert.AreEqual(MayStill, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "0x8000FFFF"), "The failed release is in the log with its raw code.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "E_UNEXPECTED"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "COMException"));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "TimedOut"), "And so is the reason the open failed.");

            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Text == "Stop playing from Test Phone" && i.Enabled));
            string atRest = TrayStatus.Tooltip(tray.Monitor.Current, tray.Coordinator.BlockStatus, tray.Settings.Current);
            Assert.AreEqual(atRest + "\n" + MayStill, tray.Context.TooltipText, "While Windows has not confirmed, the tooltip says so.");

            // The owner tries again from the menu, Windows lets go, and the menu and the tooltip say so.
            fake.ReleaseSucceedsAgain(PhoneId);
            ClickChild(tray, "Stop playing from Test Phone");

            Assert.AreEqual("This PC is no longer accepting audio from Test Phone.", tray.Cards.Shown[^1].Content.Status);
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.Context.Menu.PlayFromPhoneItems.Any(i => (i.Text ?? "").StartsWith("Stop playing", StringComparison.Ordinal)));
            Assert.AreEqual(TrayStatus.Tooltip(tray.Monitor.Current, tray.Coordinator.BlockStatus, tray.Settings.Current), tray.Context.TooltipText);
        });
    }

    [TestMethod]
    public void AStopThatWindowsDoesNotConfirmNeverSaysThisPcStoppedAccepting()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);
            fake.ReleaseFails(PhoneId);

            ClickChild(tray, "Stop playing from Test Phone");

            Assert.AreEqual(MayStill, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == "This PC is no longer accepting audio from Test Phone."));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "0x8000FFFF"));
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Text == "Stop playing from Test Phone"));
        });
    }

    [TestMethod]
    public void SwitchingOffWhenWindowsWillNotLetGoTellsTheOwnerOnce()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);
            fake.ReleaseFails(PhoneId);

            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = false });
            TrayHarness.PumpUntil(() => tray.Cards.Shown.Any(c => c.Content.Status == MayStill), "The owner was never told the phone may still be connected.");
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == MayStill));
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "0x8000FFFF"));
        });
    }

    [TestMethod]
    public void ClosingWhenWindowsWillNotLetGoRecordsEveryOutcome()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            ClickChild(tray, PhoneName);
            fake.ReleaseFails(PhoneId);

            tray.Context.Dispose();

            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "0x8000FFFF"), "A release that failed as Earshot closed is still in the log.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "may still be connected"));
        });
    }

    // Item 4 of the review. The coordinator is built off the UI thread, so the setting can change again before it is
    // ready. Here the building is held at the support check, the feature is switched off, and only then is the
    // building let go: what comes back is no longer wanted and must not be taken up.
    [TestMethod]
    public void ACoordinatorThatIsReadyAfterTheFeatureWasSwitchedOffIsNotTakenUp()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            var held = new ManualResetEventSlim(false);
            fake.SupportHeldUntil = held;
            using var tray = new TrayHarness(streamingPlatform: fake);
            tray.PumpUntilIdle();

            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = true });
            TrayHarness.PumpUntil(() => fake.SupportEntered.IsCompleted, "The coordinator was never built.");
            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = false });
            Application.DoEvents();
            held.Set();
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.Streaming.Enabled);
            Assert.IsFalse(Parent(tray).Available, "The feature is off, so the menu item is not there, whatever was being built when it was switched off.");
            Assert.IsEmpty(fake.Calls, "A coordinator that is no longer wanted reads no list.");
            Assert.IsFalse(fake.HasLinkSubscribers, "And it is disposed, not left listening.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "no longer wanted by the time it was ready"));
        });
    }

    // The same race with a second switch-on in between: the newer coordinator is the one in use, and the older one,
    // arriving late, must not replace it.
    [TestMethod]
    public void ACoordinatorThatIsReadyLateDoesNotReplaceTheNewerOne()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform first = PlatformWith(Phone(name: "From The First"));
            FakeStreamingPlatform second = PlatformWith(Phone(name: "From The Second"));
            var held = new ManualResetEventSlim(false);
            first.SupportHeldUntil = held;
            var platforms = new Queue<FakeStreamingPlatform>([first, second]);
            using var tray = new TrayHarness(streamingPlatforms: platforms);
            tray.PumpUntilIdle();

            // Each change is pumped before the next is made: a settings change is applied by a posted callback that
            // reads the settings as they are when it runs, so two changes in a row with no pump between them are seen
            // as one, and this test would then not be the race it says it is.
            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = true });
            TrayHarness.PumpUntil(() => first.SupportEntered.IsCompleted, "The first coordinator was never built.");
            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = false });
            Application.DoEvents();
            tray.Settings.Update(s => s.Streaming = s.Streaming with { Enabled = true });
            TrayHarness.PumpUntil(() => second.CallsNamed("List").Count == 1, "The second coordinator never read its list.");
            held.Set();
            tray.PumpUntilIdle();

            tray.Context.Menu.Refresh();
            CollectionAssert.AreEqual(
                Sequence.Of("From The Second", "Refresh the list"),
                tray.Context.Menu.PlayFromPhoneItems.Select(i => i.Text).ToArray());
            Assert.IsEmpty(first.Calls, "The late one reads nothing.");
            Assert.IsFalse(first.HasLinkSubscribers, "The late one is disposed.");
            Assert.IsTrue(second.HasLinkSubscribers);

            ClickChild(tray, "From The Second");
            Assert.AreEqual("Open(" + PhoneId + ")", second.Calls[^1]);
            Assert.IsEmpty(first.CallsNamed("Enable("));
        });
    }

    // Item 7 of the review. A device pinned as the one Earshot manages after the list was read is still on the menu,
    // because a pin is no reason to read the list again; the click asks the rule again and is refused.
    [TestMethod]
    public void ADevicePinnedAsTheManagedOneAfterTheListWasReadIsRefusedAtTheClick()
    {
        StaThread.Run(() =>
        {
            FakeStreamingPlatform fake = PlatformWith(Phone());
            using TrayHarness tray = On(fake);
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Text == PhoneName), "It was offered before it was pinned.");

            tray.Settings.Update(s => s.PinnedContainerId = IPhoneContainer);
            tray.PumpUntilIdle();
            tray.Context.OnPlayFromPhoneItemClicked(null, new StreamingMenuItemEventArgs(new StreamingMenuItem(PhoneName, true, false, PhoneId, StreamingMenuCommand.Play)));
            tray.PumpUntilIdle();

            Assert.IsEmpty(fake.CallsNamed("Enable("));
            Assert.IsEmpty(fake.CallsNamed("Open("));
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.Context.Menu.PlayFromPhoneItems.Any(i => i.Text == PhoneName), "And it is off the menu from then on.");
        });
    }

    [TestMethod]
    public void AnAmpersandInADeviceNameIsShownNotReadAsAnUnderline()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = On(PlatformWith(Phone(name: "Rock & Roll Phone")));
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();

            Assert.AreEqual("Rock && Roll Phone", tray.Context.Menu.PlayFromPhoneItems[0].Text);
        });
    }
}
