using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Earshot.App;
using Earshot.AudioProtection;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.Update;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the Hands-Free "microphone off" mode may and may not do. It is Protect audio quality turned off plus a note, so
// the proofs are: the setting reads and saves the way the spec says; the pure rules pick the right endpoint, page and
// words; the row and its button reach the settings and the launcher; and switching it on or off at rest changes no
// node, while the idle block, the boot block and the node selection still cover the Hands-Free service. Every id below
// is invented, and nothing here opens a window of Windows Settings.
[TestClass]
public sealed class HandsFreeMicrophoneModeTests
{
    private const string InventedCaptureId = "{0.0.1.00000000}.{aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb}";
    private static readonly Guid Container = new("5C3A9E21-4B7D-5F18-9A6C-2D8E0B4F7A13");
    private static readonly Earshot.App.CardPlace Place = Earshot.App.CardPlace.NearTray;
    private static readonly bool[] OffOnly = [false];
    private static readonly bool[] OnOnly = [true];
    private static readonly bool[] OffThenOn = [false, true];

    private static DeviceSnapshot Snapshot(SnapshotReadStatus read, params AudioEndpoint[] endpoints)
    {
        var model = new DeviceModel(Container, "AirPods", ConnectionState.Connected, endpoints);
        return new DeviceSnapshot(model, [model], DateTimeOffset.UnixEpoch) { Sequence = 1, ReadStatus = read, Resolution = TargetResolution.Pinned };
    }

    private static AudioEndpoint Capture(EndpointState state, string id = InventedCaptureId) =>
        new(id, EndpointFlow.Capture, state, "Headset", Container);

    private static AudioEndpoint Render(EndpointState state) =>
        new("{0.0.0.00000000}.{cccccccc-0000-1111-2222-dddddddddddd}", EndpointFlow.Render, state, "Headphones", Container);

    // ---- The setting

    [TestMethod]
    public void TheModeIsOffByDefaultAndForAFileWithNoMember()
    {
        using var temp = new TempFolder();
        Assert.IsFalse(new EarshotSettings().HandsFreeMicrophoneOffMode);
        Assert.IsFalse(EarshotSettings.NewInstallDefaults().HandsFreeMicrophoneOffMode, "A new install does not get the mode either.");

        File.WriteAllText(temp.File("settings.json"), "{ \"SchemaVersion\": 1, \"ProtectAudioQuality\": false }");
        var store = new JsonSettingsStore(temp.File("settings.json"), new CapturingLog());

        Assert.IsFalse(store.Current.ProtectAudioQuality);
        Assert.IsFalse(store.Current.HandsFreeMicrophoneOffMode, "Protection off alone is not the mode.");
    }

    [TestMethod]
    public void TheModeIsSavedAndReadBack()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        new JsonSettingsStore(path, new CapturingLog()).Update(s => HandsFreeMicrophoneMode.Switch(s, true));

        string text = File.ReadAllText(path);
        EarshotSettings reopened = new JsonSettingsStore(path, new CapturingLog()).Current;

        Assert.IsTrue(reopened.HandsFreeMicrophoneOffMode);
        Assert.IsFalse(reopened.ProtectAudioQuality);
        Assert.IsLessThan(text.IndexOf("HandsFreeMicrophoneOffMode", StringComparison.Ordinal), text.IndexOf("ProtectAudioQuality", StringComparison.Ordinal),
            "The two are written in that order.");
    }

    [TestMethod]
    public void AHandEditedFileWithBothSetReadsTheModeAsOffAndTheNextSaveWritesItOff()
    {
        foreach (string order in new[]
        {
            "{ \"ProtectAudioQuality\": true, \"HandsFreeMicrophoneOffMode\": true }",
            "{ \"HandsFreeMicrophoneOffMode\": true, \"ProtectAudioQuality\": true }",
        })
        {
            using var temp = new TempFolder();
            string path = temp.File("settings.json");
            File.WriteAllText(path, order);
            var store = new JsonSettingsStore(path, new CapturingLog());

            Assert.IsTrue(store.Current.ProtectAudioQuality, "Protect audio quality is the full block and wins: " + order);
            Assert.IsFalse(store.Current.HandsFreeMicrophoneOffMode, order);

            store.Update(s => s.DeviceMatch = "AirPods");
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.IsFalse(saved.RootElement.GetProperty("HandsFreeMicrophoneOffMode").GetBoolean(), "The next save writes it off: " + order);
        }
    }

    [TestMethod]
    public void AFileWithTheModeSetAndProtectionOffReadsTheModeOnInEitherMemberOrder()
    {
        foreach (string order in new[]
        {
            "{ \"ProtectAudioQuality\": false, \"HandsFreeMicrophoneOffMode\": true }",
            "{ \"HandsFreeMicrophoneOffMode\": true, \"ProtectAudioQuality\": false }",
        })
        {
            using var temp = new TempFolder();
            string path = temp.File("settings.json");
            File.WriteAllText(path, order);

            Assert.IsTrue(new JsonSettingsStore(path, new CapturingLog()).Current.HandsFreeMicrophoneOffMode, order);
        }
    }

    [TestMethod]
    public void SwitchingTheModeOnOrOffMovesProtectionTheOtherWay()
    {
        var settings = new EarshotSettings();

        HandsFreeMicrophoneMode.Switch(settings, true);
        Assert.IsFalse(settings.ProtectAudioQuality);
        Assert.IsTrue(HandsFreeMicrophoneMode.IsOn(settings));

        HandsFreeMicrophoneMode.Switch(settings, false);
        Assert.IsTrue(settings.ProtectAudioQuality);
        Assert.IsFalse(HandsFreeMicrophoneMode.IsOn(settings));
    }

    [TestMethod]
    public void ChoosingProtectionOnItsOwnEitherWayLeavesTheModeOff()
    {
        var settings = new EarshotSettings();
        HandsFreeMicrophoneMode.Switch(settings, true);

        HandsFreeMicrophoneMode.ChooseProtection(settings, true);
        Assert.IsFalse(HandsFreeMicrophoneMode.IsOn(settings));
        Assert.IsTrue(settings.ProtectAudioQuality);

        HandsFreeMicrophoneMode.Switch(settings, true);
        HandsFreeMicrophoneMode.ChooseProtection(settings, false);
        Assert.IsFalse(HandsFreeMicrophoneMode.IsOn(settings), "Protection off from the menu is plain protection off, not the mode.");
        Assert.IsFalse(settings.ProtectAudioQuality);
    }

    // ---- The pure rules

    [TestMethod]
    public void TheCaptureEndpointIsTheOneToOpenAndAnActiveOneBeatsAnUnpluggedOneBeatsADisabledOne()
    {
        AudioEndpoint active = Capture(EndpointState.Active, "{0.0.1.00000000}.{11111111-0000-1111-2222-bbbbbbbbbbbb}");
        AudioEndpoint unplugged = Capture(EndpointState.Unplugged, "{0.0.1.00000000}.{22222222-0000-1111-2222-bbbbbbbbbbbb}");
        AudioEndpoint disabled = Capture(EndpointState.Disabled, "{0.0.1.00000000}.{33333333-0000-1111-2222-bbbbbbbbbbbb}");
        AudioEndpoint notPresent = Capture(EndpointState.NotPresent, "{0.0.1.00000000}.{44444444-0000-1111-2222-bbbbbbbbbbbb}");

        Assert.AreSame(active, HandsFreeMicrophoneMode.CaptureEndpoint(Snapshot(SnapshotReadStatus.Ok, disabled, unplugged, active, Render(EndpointState.Active))));
        Assert.AreSame(unplugged, HandsFreeMicrophoneMode.CaptureEndpoint(Snapshot(SnapshotReadStatus.Ok, disabled, unplugged)));
        Assert.AreSame(disabled, HandsFreeMicrophoneMode.CaptureEndpoint(Snapshot(SnapshotReadStatus.Ok, notPresent, disabled)));
        Assert.IsNull(HandsFreeMicrophoneMode.CaptureEndpoint(Snapshot(SnapshotReadStatus.Ok, notPresent, Render(EndpointState.Active))));
        Assert.IsNull(HandsFreeMicrophoneMode.CaptureEndpoint(new DeviceSnapshot(null, [], DateTimeOffset.UnixEpoch)));
    }

    [TestMethod]
    public void TheRowSaysConnectFirstWithNoMicrophoneOpenSettingsWithOneAndOffInWindowsWhenItIsDisabled()
    {
        MicrophoneRow none = HandsFreeMicrophoneMode.Describe(Snapshot(SnapshotReadStatus.Ok, Render(EndpointState.Active)));
        Assert.AreEqual(MicrophoneRowState.ConnectFirst, none.State);
        Assert.AreEqual(HandsFreeMicrophoneMode.SoundDevicesUri, none.SettingsUri);

        MicrophoneRow there = HandsFreeMicrophoneMode.Describe(Snapshot(SnapshotReadStatus.Ok, Capture(EndpointState.Active)));
        Assert.AreEqual(MicrophoneRowState.OpenSettings, there.State);
        Assert.AreEqual("ms-settings:sound-properties?endpointId=" + InventedCaptureId, there.SettingsUri);

        MicrophoneRow off = HandsFreeMicrophoneMode.Describe(Snapshot(SnapshotReadStatus.Ok, Capture(EndpointState.Disabled)));
        Assert.AreEqual(MicrophoneRowState.OffInWindows, off.State);
    }

    [TestMethod]
    public void AFailedReadNeverTellsThePersonToConnect()
    {
        MicrophoneRow row = HandsFreeMicrophoneMode.Describe(Snapshot(SnapshotReadStatus.Failed));

        Assert.AreEqual(MicrophoneRowState.OpenSettings, row.State);
        Assert.AreEqual(HandsFreeMicrophoneMode.SoundDevicesUri, row.SettingsUri);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("{0.0.0.00000000}.{aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb}")]
    [DataRow("{0.0.1.00000000}.capture")]
    [DataRow("{0.0.1.00000000}.{aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb}&other=1")]
    [DataRow("{0.0.1.00000000}.{aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb} ")]
    [DataRow("ms-settings:privacy-microphone")]
    [DataRow("\\\\?\\SWD#MMDEVAPI#{0.0.1.00000000}.{aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb}#{cccccccc-2222-3333-4444-dddddddddddd}")]
    public void AnIdNotInTheFormSettingsTakesOpensTheListOfSoundDevices(string? id)
    {
        Assert.IsFalse(HandsFreeMicrophoneMode.IsCaptureEndpointId(id));

        string uri = id is null ? HandsFreeMicrophoneMode.SettingsUriFor(null) : HandsFreeMicrophoneMode.SettingsUriFor(Capture(EndpointState.Active, id));

        Assert.AreEqual(HandsFreeMicrophoneMode.SoundDevicesUri, uri);
    }

    [TestMethod]
    public void AWellFormedCaptureIdIsAcceptedInEitherCase()
    {
        Assert.IsTrue(HandsFreeMicrophoneMode.IsCaptureEndpointId(InventedCaptureId));
        Assert.IsTrue(HandsFreeMicrophoneMode.IsCaptureEndpointId("{0.0.1.0000ABCD}.{AAAAAAAA-0000-1111-2222-BBBBBBBBBBBB}"));
    }

    // ---- At rest

    // The node selection the idle block and the boot block both use: the Hands-Free service's node is a target and its
    // audio child is not, whatever the mode says (the mode is not an input to it).
    [TestMethod]
    public void TheHandsFreeServiceNodeIsStillADisableTargetAndItsAudioChildIsNot()
    {
        const string address = "0A1B2C3D4E8C";
        const string handsFreeService = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000";
        const string handsFreeAudioChild = @"BTHHFENUM\BTHHFPAUDIO\c&2b3c4d5e&1&97";

        Assert.IsTrue(NodeMatch.IsDisableTarget(handsFreeService, Container, Container, address));
        Assert.IsFalse(NodeMatch.IsDisableTarget(handsFreeAudioChild, Container, Container, address));
    }

    [TestMethod]
    public void WithTheModeOnTheBlockStillGoesStraightToTheNodesAndTheServicesAreLeftAlone()
    {
        // Protect is false, which is what the mode saves. The Hands-Free service is installed (NotProtected).
        var facts = new ProtectionFacts(NodePhase.Allowed, AudioProtectionState.NotProtected, Protect: false, IntentPending: false);

        Assert.AreEqual(ProtectionAction.BlockNodes, ProtectionPolicy.Next(ProtectionGoal.Block, facts, new ProtectionProgress()));
        Assert.AreEqual(ProtectionAction.Done, ProtectionPolicy.Next(ProtectionGoal.Block, facts, new ProtectionProgress().After(ProtectionAction.BlockNodes)));
    }

    [TestMethod]
    public void SwitchingTheModeOnWhileTheNodesAreBlockedIsKeptAsIntentAndChangesNoNode()
    {
        foreach (NodePhase phase in new[] { NodePhase.Blocked, NodePhase.Mixed })
        {
            var facts = new ProtectionFacts(phase, AudioProtectionState.Protected, Protect: false, IntentPending: false);

            ProtectionAction first = ProtectionPolicy.Next(ProtectionGoal.SetProtection, facts, new ProtectionProgress());

            Assert.AreEqual(ProtectionAction.StoreIntent, first, phase + ": the change is kept, not applied.");
            Assert.AreEqual(ProtectionAction.Done, ProtectionPolicy.Next(ProtectionGoal.SetProtection, facts, new ProtectionProgress().After(first)));
        }

        var unknown = new ProtectionFacts(NodePhase.Unknown, AudioProtectionState.Unknown, Protect: false, IntentPending: false);
        Assert.AreEqual(ProtectionAction.KeepIntent, ProtectionPolicy.Next(ProtectionGoal.SetProtection, unknown, new ProtectionProgress()));
    }

    [TestMethod]
    public void AllowingWithTheModeOnEnablesTheNodesAndLeavesTheServicesAsTheyAre()
    {
        var blocked = new ProtectionFacts(NodePhase.Blocked, AudioProtectionState.NotProtected, Protect: false, IntentPending: false);
        Assert.AreEqual(ProtectionAction.AllowNodes, ProtectionPolicy.Next(ProtectionGoal.Allow, blocked, new ProtectionProgress()));

        var allowed = blocked with { Nodes = NodePhase.Allowed };
        ProtectionProgress afterAllow = new ProtectionProgress().After(ProtectionAction.AllowNodes);
        Assert.AreEqual(ProtectionAction.Done, ProtectionPolicy.Next(ProtectionGoal.Allow, allowed, afterAllow), "No service change: protection is not wanted.");
    }

    [TestMethod]
    public void WithTheModeOnTheIdleBlockStillBlocksTheNodesWithTheHandsFreeServiceInstalled()
    {
        using var h = new CoordinatorHarness();
        h.Settings.Update(s => HandsFreeMicrophoneMode.Switch(s, true));
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        string[] blockOnly = ["block"];
        CollectionAssert.AreEqual(blockOnly, h.Trace, "The block is sent and no service is touched.");
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
        Assert.IsEmpty(h.Protection.Applies);
    }

    [TestMethod]
    public void SwitchingTheModeOnOrOffAtRestEnablesNothingAndConnectsNothing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Protection.OnApply = (_, _) => Task.FromResult(new ControllerResult(
            OpStatus.NotAttempted, "Saved. It applies when the AirPods are allowed.", [StepOutcomes.NotAttempted("protect", "A device node is disabled.")]));

        h.Settings.Update(s => HandsFreeMicrophoneMode.Switch(s, true));
        ControllerResult on = h.SetProtection(protect: false);
        h.Settings.Update(s => HandsFreeMicrophoneMode.Switch(s, false));
        ControllerResult off = h.SetProtection(protect: true);

        Assert.AreEqual(OpStatus.NotAttempted, on.Status);
        Assert.AreEqual(OpStatus.NotAttempted, off.Status);
        Assert.IsEmpty(h.Block.Calls, "No allow, no block: the nodes stay as they were.");
        Assert.IsEmpty(h.Connection.Calls, "Nothing is connected or disconnected.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        CollectionAssert.AreEqual(OffThenOn, h.Protection.Applies, "Only the protect verb runs, and the gate keeps it while a node is disabled.");
    }

    // ---- The menu

    [TestMethod]
    public void TheMenuCaveatNamesTheModeWhileItIsOn()
    {
        EarshotSettings on = new();
        HandsFreeMicrophoneMode.Switch(on, true);

        Assert.AreEqual(
            MenuModel.ProtectCaveatMicrophoneOffMode,
            MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, on, busy: false, StartupState.Off).ProtectCaveat.Text);
        Assert.AreEqual(
            MenuModel.ProtectCaveat,
            MenuModel.Build(Phase1Fixtures.NoDevice(), null, null, new EarshotSettings(), busy: false, StartupState.Off).ProtectCaveat.Text);
    }

    // ---- The launcher

    [TestMethod]
    public async Task OnlyAWindowsSettingsLinkIsEverOpened()
    {
        Assert.IsTrue(WindowsSettingsLauncher.IsSettingsUri(HandsFreeMicrophoneMode.SoundDevicesUri));
        Assert.IsTrue(WindowsSettingsLauncher.IsSettingsUri("ms-settings:sound-properties?endpointId=" + InventedCaptureId));
        Assert.IsFalse(WindowsSettingsLauncher.IsSettingsUri("https://example.invalid/"));
        Assert.IsFalse(WindowsSettingsLauncher.IsSettingsUri(@"C:\Windows\System32\cmd.exe"));
        Assert.IsFalse(WindowsSettingsLauncher.IsSettingsUri(null));

        var log = new CapturingLog();
        bool opened = await new WindowsSettingsLauncher(log).OpenAsync("https://example.invalid/", CancellationToken.None);

        Assert.IsFalse(opened, "Refused before any launch.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "not a Windows Settings link"));
    }

    [TestMethod]
    public async Task SafeModeRefusesToOpenSoundSettingsAndLogsIt()
    {
        var log = new CapturingLog();

        bool opened = await new SafeSettingsLauncher(log).OpenAsync(HandsFreeMicrophoneMode.SoundDevicesUri, CancellationToken.None);

        Assert.IsFalse(opened);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode"));
    }

    // ---- The row and the tray

    [TestMethod]
    public void TheHostSwitchesTheModeThroughTheMenuItemsPathAndBackAgain()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => s.ProtectAudioQuality = true);
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;
            Assert.IsFalse(host.ReadSettings().HandsFreeMicrophoneOff, "Off until the person turns it on.");

            host.SetHandsFreeMicrophoneOff(true, Place);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.HandsFreeMicrophoneOffMode);
            Assert.IsFalse(tray.Settings.Current.ProtectAudioQuality);
            Assert.IsTrue(host.ReadSettings().HandsFreeMicrophoneOff);
            CollectionAssert.AreEqual(OffOnly, tray.Protection.Calls, "Protection was asked to turn off, by the menu item's own path.");

            host.SetHandsFreeMicrophoneOff(true, Place);
            tray.PumpUntilIdle();
            Assert.HasCount(1, tray.Protection.Calls, "Already on: nothing more is asked.");

            host.SetHandsFreeMicrophoneOff(false, Place);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.HandsFreeMicrophoneOffMode);
            Assert.IsTrue(tray.Settings.Current.ProtectAudioQuality);
            CollectionAssert.AreEqual(OffThenOn, tray.Protection.Calls);
        });
    }

    [TestMethod]
    public void TurningProtectionOnFromTheMenuWhileTheModeIsOnTurnsTheModeOff()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => HandsFreeMicrophoneMode.Switch(s, true));
            Assert.IsTrue(tray.Settings.Current.HandsFreeMicrophoneOffMode);

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.ProtectAudioQuality);
            Assert.IsFalse(tray.Settings.Current.HandsFreeMicrophoneOffMode);
            CollectionAssert.AreEqual(OnOnly, tray.Protection.Calls);
        });
    }

    [TestMethod]
    public void TurningProtectionOffFromTheMenuIsPlainProtectionOffNotTheMode()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => s.ProtectAudioQuality = true);

            tray.ClickMenu(MenuModel.ProtectAudioQuality);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.ProtectAudioQuality);
            Assert.IsFalse(tray.Settings.Current.HandsFreeMicrophoneOffMode);
        });
    }

    [TestMethod]
    public void TheHostReadsWhereTheMicrophoneStandsAndOpensTheRightPage()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var launcher = new FakeSettingsLauncher();
            DeviceSnapshot snapshot = Snapshot(SnapshotReadStatus.Ok, Capture(EndpointState.Active));
            using var tray = new UpdateTrayHarness(snapshot: snapshot, settingsLauncher: launcher, settings: s => HandsFreeMicrophoneMode.Switch(s, true));
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            CardSettingsValues values = host.ReadSettings();
            Assert.IsTrue(values.HandsFreeMicrophoneOff);
            Assert.AreEqual(MicrophoneRowState.OpenSettings, values.MicrophoneState);

            host.OpenSoundSettings(Place);
            tray.PumpUntilIdle();

            string[] opened = ["ms-settings:sound-properties?endpointId=" + InventedCaptureId];
            CollectionAssert.AreEqual(opened, launcher.Opened);
        });
    }

    [TestMethod]
    public void WithNoMicrophoneYetTheButtonOpensTheListAndAFailedOpenSaysSo()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var launcher = new FakeSettingsLauncher { Result = false };
            using var tray = new UpdateTrayHarness(
                snapshot: Snapshot(SnapshotReadStatus.Ok, Render(EndpointState.Active)), settingsLauncher: launcher,
                settings: s => HandsFreeMicrophoneMode.Switch(s, true));
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;
            Assert.AreEqual(MicrophoneRowState.ConnectFirst, host.ReadSettings().MicrophoneState);

            host.OpenSoundSettings(Place);
            tray.PumpUntilIdle();

            string[] opened = [HandsFreeMicrophoneMode.SoundDevicesUri];
            CollectionAssert.AreEqual(opened, launcher.Opened);
            Assert.AreEqual(WidgetCopy.SoundSettingsNotOpened, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void WithoutAFakeLauncherATestRunNeverOpensSettings()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => HandsFreeMicrophoneMode.Switch(s, true));

            tray.Context.WidgetCardHostForTest.OpenSoundSettings(Place);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Safe mode"), "The redirected data root gets the refusing launcher.");
        });
    }
}

// Records what it was asked to open and answers with a chosen result. Opens nothing.
internal sealed class FakeSettingsLauncher : ISettingsLauncher
{
    public List<string> Opened { get; } = [];

    public bool Result { get; set; } = true;

    public Task<bool> OpenAsync(string uri, CancellationToken ct)
    {
        Opened.Add(uri);
        return Task.FromResult(Result);
    }
}

// The settings page's Microphone off row: where it sits, what it says in each state, how it is named for a screen reader,
// and that its switch and its button reach the host. Drawn and clicked through the card's own code on a private desktop.
[TestClass]
public sealed class HandsFreeMicrophoneRowTests
{
    private static CardSettingsValues Values(bool on, MicrophoneRowState state) =>
        FakeCardHost.Defaults() with { HandsFreeMicrophoneOff = on, MicrophoneState = state };

    private static SettingsLayout Layout(CardSettingsValues values, int dpi = 96)
    {
        SettingsLayout? layout = null;
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(values), dpi);
            layout = card.CurrentSettingsLayout;
        });
        return layout!;
    }

    private static SettingsItem[] Rows(SettingsLayout layout) => layout.Items.Where(i => i.Kind == SettingsItemKind.Row).ToArray();

    [TestMethod]
    public void TheRowIsAlwaysThereWithNoNoteWhileTheModeIsOffAndNoSoundSettingsRow()
    {
        SettingsItem[] rows = Rows(Layout(Values(false, MicrophoneRowState.OpenSettings)));

        SettingsItem mic = rows.Single(r => r.Row == SettingsRowId.MicrophoneOff);
        Assert.AreEqual("Microphone off", mic.Label);
        Assert.IsNull(mic.Sub);
        Assert.IsFalse(rows.Any(r => r.Row == SettingsRowId.SoundSettings));
        Assert.AreEqual(SettingsRowId.HandBack, rows[Array.IndexOf(rows, mic) - 1].Row, "Beside the other protection setting, after Hand back.");
    }

    [TestMethod]
    public void WhileTheModeIsOnTheNoteSaysWhatToDoNextAndTheButtonIsOfferedUnlessWindowsHasItOff()
    {
        SettingsItem[] open = Rows(Layout(Values(true, MicrophoneRowState.OpenSettings)));
        Assert.AreEqual("In Sound settings, set the AirPods microphone to Don't allow.", open.Single(r => r.Row == SettingsRowId.MicrophoneOff).Sub);
        SettingsItem button = open.Single(r => r.Row == SettingsRowId.SoundSettings);
        Assert.AreEqual(SettingsRowId.MicrophoneOff, open[Array.IndexOf(open, button) - 1].Row);

        SettingsItem[] connect = Rows(Layout(Values(true, MicrophoneRowState.ConnectFirst)));
        Assert.AreEqual("Connect the AirPods, then open sound settings.", connect.Single(r => r.Row == SettingsRowId.MicrophoneOff).Sub);
        Assert.IsTrue(connect.Any(r => r.Row == SettingsRowId.SoundSettings), "The button then opens the list of sound devices.");

        SettingsItem[] off = Rows(Layout(Values(true, MicrophoneRowState.OffInWindows)));
        Assert.AreEqual("Microphone is off in Windows", off.Single(r => r.Row == SettingsRowId.MicrophoneOff).Sub);
        Assert.IsFalse(off.Any(r => r.Row == SettingsRowId.SoundSettings), "Nothing is left to open.");
    }

    [TestMethod]
    public void EachRowIsAnIconAndAtMostThreeWordsWithATooltipAndAnAccessibleName()
    {
        foreach (SettingsRowId row in new[] { SettingsRowId.MicrophoneOff, SettingsRowId.SoundSettings })
        {
            SettingsRowInfo info = SettingsRows.For(row);
            SettingsItem item = Rows(Layout(Values(true, MicrophoneRowState.OpenSettings))).Single(r => r.Row == row);

            Assert.AreNotEqual('\0', info.Glyph, row + " has an icon.");
            Assert.AreEqual(info.Glyph, item.Glyph);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Tip), row + " has a tooltip.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Name), row + " has an accessible name.");
            Assert.IsLessThanOrEqualTo(3, item.Label.Split(' ').Length, row + " label: " + item.Label);
            Assert.IsFalse(item.Label.Contains('\u2014', StringComparison.Ordinal));
        }

        Assert.AreEqual(FluentGlyphs.MicOff, SettingsRows.For(SettingsRowId.MicrophoneOff).Glyph);
        Assert.AreEqual("Open sound settings", SettingsRows.NameOf(SettingsRowId.SoundSettings, SettingsPart.Button));
        Assert.AreEqual("Microphone off mode", SettingsRows.NameOf(SettingsRowId.MicrophoneOff, SettingsPart.Toggle));
    }

    [TestMethod]
    public void TheRowsFitTheCardAtEveryScaleInEveryState()
    {
        foreach (int dpi in CardKit.Scales)
        {
            foreach (MicrophoneRowState state in Enum.GetValues<MicrophoneRowState>())
            {
                SettingsLayout layout = Layout(Values(true, state), dpi);
                foreach (SettingsItem item in layout.Items)
                {
                    Assert.IsGreaterThanOrEqualTo(0, item.Bounds.Left);
                    Assert.IsLessThanOrEqualTo(layout.Frame.Width, item.Bounds.Right, item.Row + " at " + dpi + " dpi, " + state);
                }

                foreach (SettingsItem row in Rows(layout).Where(r => r.Row is SettingsRowId.MicrophoneOff or SettingsRowId.SoundSettings))
                {
                    Assert.IsFalse(row.A.IsEmpty, row.Row + " has a control at " + dpi + " dpi.");
                    Assert.IsLessThanOrEqualTo(layout.Frame.Width, row.A.Right);
                }
            }
        }
    }

    [TestMethod]
    public void TheKeyboardReachesTheSwitchAndTheButtonAndTheyRaiseTheirOwnChanges()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(Values(true, MicrophoneRowState.OpenSettings)), 96);
            Assert.IsTrue(card.CurrentSettingsLayout!.Targets.Contains(new SettingsTarget(SettingsRowId.MicrophoneOff, SettingsPart.Toggle)));
            Assert.IsTrue(card.CurrentSettingsLayout.Targets.Contains(new SettingsTarget(SettingsRowId.SoundSettings, SettingsPart.Button)));
            var raised = new List<SettingChange>();
            card.SettingChanged += (_, change) => raised.Add(change);

            card.FocusSettingsTarget(new SettingsTarget(SettingsRowId.MicrophoneOff, SettingsPart.Toggle));
            card.HandleSettingsKey(Keys.Space);
            card.FocusSettingsTarget(new SettingsTarget(SettingsRowId.SoundSettings, SettingsPart.Button));
            card.HandleSettingsKey(Keys.Enter);

            Assert.HasCount(2, raised);
            Assert.AreEqual(new ToggleChange(SettingsRowId.MicrophoneOff, false), raised[0], "The row is on, so the switch turns it off.");
            Assert.IsInstanceOfType<OpenSoundSettingsRequest>(raised[1]);
        });
    }

    [TestMethod]
    public void ClickingTheSwitchAndTheButtonReachesTheHostThroughThePresenter()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { Values = Values(false, MicrophoneRowState.OpenSettings) };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            Assert.AreEqual(WidgetCardView.Settings, presenter.ViewForTest);

            CardKit.Click(card, CardKit.Part(card, SettingsRowId.MicrophoneOff, SettingsPart.Toggle));
            CardKit.AssertCalls(host, "micOff:True");
            Assert.IsTrue(host.Values.HandsFreeMicrophoneOff);
            Assert.IsNotNull(CardKit.Row(card, SettingsRowId.SoundSettings), "Turned on, the page offers the button.");

            CardKit.Click(card, CardKit.Part(card, SettingsRowId.SoundSettings, SettingsPart.Button));
            CardKit.AssertCalls(host, "micOff:True", "openSound");
        });
    }

    [TestMethod]
    public void TheCopyUsesBritishEnglishPlainWordsAndNoEmDash()
    {
        string[] words =
        [
            WidgetCopy.SettingsMicOff, WidgetCopy.SettingsSoundSettings, WidgetCopy.OpenButton, WidgetCopy.TipMicOff, WidgetCopy.TipSoundSettings,
            WidgetCopy.NameMicOff, WidgetCopy.NameSoundSettings, WidgetCopy.MicGuidance, WidgetCopy.MicConnectFirst, WidgetCopy.MicOffInWindows,
            WidgetCopy.SoundSettingsNotOpened, MenuModel.ProtectCaveatMicrophoneOffMode,
        ];
        foreach (string text in words)
        {
            Assert.IsFalse(text.Contains('\u2014', StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains('\u2013', StringComparison.Ordinal), text);
            Assert.IsFalse(text.Contains("--", StringComparison.Ordinal), text);
        }
    }
}
