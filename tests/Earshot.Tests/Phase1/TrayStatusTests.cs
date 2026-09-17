using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

[TestClass]
public sealed class TrayStatusTests
{
    [TestMethod]
    public void TooltipWithNoDeviceSaysNotFoundWithTheMatchString()
    {
        Assert.AreEqual("Earshot: AirPods - not found", TrayStatus.Tooltip(NotFound(), block: null, Settings()));
        Assert.AreEqual("Earshot: AirPods - unknown", TrayStatus.Tooltip(NoDevice(), block: null, Settings()),
            "Nothing has been read yet, so nothing is claimed.");
        Assert.AreEqual("Earshot: AirPods - unknown", TrayStatus.Tooltip(ReadFailed(), block: null, Settings()),
            "The read failed, so nothing is claimed.");
    }

    [TestMethod]
    [DataRow(ConnectionState.Connected, "connected")]
    [DataRow(ConnectionState.Disconnecting, "connected")]
    [DataRow(ConnectionState.Disconnected, "disconnected")]
    [DataRow(ConnectionState.Connecting, "disconnected")]
    [DataRow(ConnectionState.Unknown, "disconnected")]
    public void TooltipNamesTheDeviceAndItsState(ConnectionState connection, string word)
    {
        Assert.AreEqual(
            "Earshot: Jonathan\u2019s AirPods Pro - " + word,
            TrayStatus.Tooltip(Target(connection), Block(BlockState.Allowed), Settings()));
    }

    [TestMethod]
    public void TooltipSaysBlockedWhenTheNodesAreBlocked()
    {
        Assert.AreEqual("Earshot: AirPods - blocked", TrayStatus.Tooltip(NoDevice(), Block(BlockState.Blocked), Settings()));
        Assert.AreEqual("Earshot: Jonathan\u2019s AirPods Pro - blocked",
            TrayStatus.Tooltip(Target(ConnectionState.Disconnected), Block(BlockState.Blocked), Settings()));
        Assert.AreEqual("Earshot: Jonathan\u2019s AirPods Pro - connected",
            TrayStatus.Tooltip(Target(ConnectionState.Connected), Block(BlockState.Blocked), Settings()));
    }

    [TestMethod]
    public void TooltipUsesAPlainHyphen()
    {
        string tooltip = TrayStatus.Tooltip(Target(ConnectionState.Connected), null, Settings());

        Assert.IsFalse(tooltip.Contains((char)0x2014, StringComparison.Ordinal));
        Assert.IsFalse(tooltip.Contains((char)0x2013, StringComparison.Ordinal));
        Assert.Contains(" - ", tooltip);
    }

    [TestMethod]
    public void TooltipFitsIn127CharactersAndKeepsItsState()
    {
        string longName = new string('A', 300);

        string tooltip = TrayStatus.Tooltip(Target(ConnectionState.Connected, name: longName), null, Settings());

        Assert.AreEqual(TrayStatus.MaxTooltipLength, tooltip.Length);
        Assert.StartsWith("Earshot: AAAA", tooltip);
        Assert.EndsWith(" - connected", tooltip);
    }

    [TestMethod]
    public void TooltipNeverSplitsASurrogatePair()
    {
        // "Earshot: " is 9 and " - connected" is 12, so the name gets 106 units. Put a pair across 105/106.
        string name = new string('A', 105) + "\U0001F3A7" + "tail";

        string tooltip = TrayStatus.Tooltip(Target(ConnectionState.Connected, name: name), null, Settings());

        Assert.AreEqual("Earshot: " + new string('A', 105) + " - connected", tooltip);
    }

    [TestMethod]
    public void TruncateLeavesShortTextAlone()
    {
        Assert.AreEqual("abc", TrayStatus.Truncate("abc", 3));
        Assert.AreEqual("ab", TrayStatus.Truncate("abc", 2));
        Assert.AreEqual("", TrayStatus.Truncate("\U0001F3A7", 1));
    }

    [TestMethod]
    public void GlyphShowsBusyWhileAToggleIsInFlightOrTheLinkIsChanging()
    {
        Assert.AreEqual(GlyphState.Busy, TrayStatus.Glyph(NoDevice(), null, Settings(), toggleInFlight: true));
        Assert.AreEqual(GlyphState.Busy, TrayStatus.Glyph(Target(ConnectionState.Connected), null, Settings(), toggleInFlight: true));
        Assert.AreEqual(GlyphState.Busy, TrayStatus.Glyph(Target(ConnectionState.Connecting), null, Settings(), toggleInFlight: false));
        Assert.AreEqual(GlyphState.Busy, TrayStatus.Glyph(Target(ConnectionState.Disconnecting), null, Settings(), toggleInFlight: false));
    }

    [TestMethod]
    public void GlyphFollowsConnectionThenBlock()
    {
        Assert.AreEqual(GlyphState.Connected, TrayStatus.Glyph(Target(ConnectionState.Connected), Block(BlockState.Blocked), Settings(), false));
        Assert.AreEqual(GlyphState.Blocked, TrayStatus.Glyph(NoDevice(), Block(BlockState.Blocked), Settings(), false));
        Assert.AreEqual(GlyphState.Blocked, TrayStatus.Glyph(Target(ConnectionState.Disconnected), Block(BlockState.Blocked), Settings(), false));
        Assert.AreEqual(GlyphState.Disconnected, TrayStatus.Glyph(Target(ConnectionState.Disconnected), Block(BlockState.Allowed), Settings(), false));
        Assert.AreEqual(GlyphState.Disconnected, TrayStatus.Glyph(NoDevice(), Block(BlockState.NotSetUp), Settings(), false));
        Assert.AreEqual(GlyphState.Disconnected, TrayStatus.Glyph(NoDevice(), Block(BlockState.Mixed), Settings(), false));
    }

    [TestMethod]
    public void ATargetOutsideThePinnedContainerIsShownAsNotFound()
    {
        EarshotSettings pinned = Settings(s => s.PinnedContainerId = AirPodsContainer);
        DeviceSnapshot iPhone = Target(ConnectionState.Connected, IPhoneContainer, "iPhone");

        Assert.IsNull(TrayStatus.ActiveTarget(iPhone, pinned));
        Assert.AreEqual("Earshot: AirPods - not found", TrayStatus.Tooltip(iPhone, null, pinned));
        Assert.AreEqual("Earshot: AirPods - blocked", TrayStatus.Tooltip(iPhone, Block(BlockState.Blocked), pinned));
        Assert.AreEqual(GlyphState.Disconnected, TrayStatus.Glyph(iPhone, null, pinned, toggleInFlight: false));
        Assert.AreEqual("AirPods", TrayStatus.DeviceName(iPhone, pinned));
        Assert.AreEqual(TrayStatus.NotFoundMessage(pinned), TrayStatus.CardStatus(iPhone, null, pinned));
    }

    [TestMethod]
    public void ATargetInThePinnedContainerOrWithNothingPinnedIsActive()
    {
        DeviceSnapshot airPods = Target(ConnectionState.Connected);

        Assert.AreSame(airPods.Target, TrayStatus.ActiveTarget(airPods, Settings(s => s.PinnedContainerId = AirPodsContainer)));
        Assert.AreSame(airPods.Target, TrayStatus.ActiveTarget(airPods, Settings()));
        Assert.IsNull(TrayStatus.ActiveTarget(NoDevice(), Settings()));
    }

    [TestMethod]
    public void OnlyNotSetUpNeedsSetUp()
    {
        Assert.IsTrue(TrayStatus.NeedsSetUp(Block(BlockState.NotSetUp)));
        Assert.IsFalse(TrayStatus.NeedsSetUp(null));
        foreach (BlockState state in new[] { BlockState.Allowed, BlockState.Blocked, BlockState.Mixed, BlockState.Unknown, BlockState.NotFound })
        {
            Assert.IsFalse(TrayStatus.NeedsSetUp(Block(state)), state.ToString());
        }
    }

    [TestMethod]
    public void CardStatusUsesTheDesignedLines()
    {
        Assert.AreEqual("Connected", TrayStatus.CardStatus(Target(ConnectionState.Connected), null, Settings()));
        Assert.AreEqual("Disconnected", TrayStatus.CardStatus(Target(ConnectionState.Disconnected), null, Settings()));
        Assert.AreEqual("Blocked at boot", TrayStatus.CardStatus(NoDevice(), Block(BlockState.Blocked), Settings()));
        Assert.AreEqual(
            "AirPods not found. Connect them once, or choose your device from the menu.",
            TrayStatus.CardStatus(NotFound(), null, Settings()));
        Assert.AreEqual(
            "Buds not found. Connect them once, or choose your device from the menu.",
            TrayStatus.CardStatus(NotFound(), null, Settings(s => s.DeviceMatch = "Buds")));
        Assert.AreEqual(TrayStatus.CardNotKnown, TrayStatus.CardStatus(NoDevice(), null, Settings()),
            "Nothing has been read yet, so the card does not say the AirPods are missing.");
    }

    [TestMethod]
    public void DeviceNameFallsBackToTheMatchString()
    {
        Assert.AreEqual(AirPodsName, TrayStatus.DeviceName(Target(ConnectionState.Connected), Settings()));
        Assert.AreEqual("AirPods", TrayStatus.DeviceName(NoDevice(), Settings()));
        Assert.AreEqual("AirPods", TrayStatus.DeviceName(Target(ConnectionState.Connected, name: " "), Settings()));
    }

    [TestMethod]
    public void WithNothingPinnedAndNoDeviceThereIsNoIntent()
    {
        Assert.IsNull(TrayStatus.Intent(NoDevice(), Settings()));
    }

    [TestMethod]
    public void WithoutAPinTheResolvedTargetIsToggled()
    {
        ToggleIntent? connect = TrayStatus.Intent(Target(ConnectionState.Disconnected), Settings());
        ToggleIntent? disconnect = TrayStatus.Intent(Target(ConnectionState.Connected), Settings());

        Assert.IsNotNull(connect);
        Assert.IsTrue(connect.Connect);
        Assert.AreEqual(AirPodsContainer, connect.Container);
        Assert.AreEqual(AirPodsName, connect.DeviceName);
        Assert.IsNotNull(disconnect);
        Assert.IsFalse(disconnect.Connect);
    }

    [TestMethod]
    public void ThePinnedContainerWinsOverAnotherTarget()
    {
        EarshotSettings pinned = Settings(s => s.PinnedContainerId = AirPodsContainer);

        ToggleIntent? intent = TrayStatus.Intent(Target(ConnectionState.Connected, IPhoneContainer), pinned);

        Assert.IsNotNull(intent);
        Assert.AreEqual(AirPodsContainer, intent.Container);
        Assert.IsTrue(intent.Connect, "The pinned device is not the one reported connected.");
    }

    [TestMethod]
    public void APinnedDeviceThatIsBlockedAndNotResolvedStillGetsAConnect()
    {
        EarshotSettings pinned = Settings(s => s.PinnedContainerId = AirPodsContainer);

        ToggleIntent? intent = TrayStatus.Intent(NoDevice(), pinned);

        Assert.IsNotNull(intent);
        Assert.IsTrue(intent.Connect);
        Assert.AreEqual(AirPodsContainer, intent.Container);
        Assert.AreEqual("AirPods", intent.DeviceName);
    }

    [TestMethod]
    public void ThePcContainerIsNeverToggled()
    {
        Assert.IsNull(TrayStatus.Intent(Target(ConnectionState.Connected, NodeMatch.PcContainer), Settings()));
        Assert.IsNull(TrayStatus.Intent(Target(ConnectionState.Connected, Guid.Empty), Settings()));
    }

    [TestMethod]
    public void AMatchingTargetIsPinnedWhenNothingIsPinned()
    {
        Assert.AreEqual(AirPodsContainer, TrayStatus.PinCandidate(Target(ConnectionState.Connected), Settings())?.ContainerId);
    }

    [TestMethod]
    public void NothingIsPinnedFromAReadThatDidNotWork()
    {
        Assert.IsNull(TrayStatus.PinCandidate(ReadFailed(), Settings()), "A failed read is not a sighting.");
        Assert.IsNull(TrayStatus.PinCandidate(NoDevice(), Settings()), "Nothing has been read yet.");
    }

    [TestMethod]
    public void APinnedContainerWithoutAnAddressIsCompleted()
    {
        EarshotSettings settings = Settings(s => s.PinnedContainerId = AirPodsContainer);

        Assert.AreEqual(AirPodsContainer, TrayStatus.PinCandidate(Target(ConnectionState.Disconnected), settings)?.ContainerId);
    }

    [TestMethod]
    public void PinningLeavesAnExistingPinAlone()
    {
        EarshotSettings full = Settings(s =>
        {
            s.PinnedContainerId = AirPodsContainer;
            s.PinnedAddress = AirPodsAddress;
        });
        EarshotSettings other = Settings(s => s.PinnedContainerId = IPhoneContainer);

        Assert.IsNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected), full));
        Assert.IsNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected), other));
    }

    [TestMethod]
    public void OnlyANameMatchInARealContainerIsPinned()
    {
        Assert.IsNull(TrayStatus.PinCandidate(NoDevice(), Settings()));
        Assert.IsNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected, name: "iPhone"), Settings()));
        Assert.IsNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected, NodeMatch.PcContainer), Settings()));
        Assert.IsNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected, Guid.Empty), Settings()));
        Assert.IsNotNull(TrayStatus.PinCandidate(Target(ConnectionState.Connected, name: "jonathan\u2019s airpods pro"), Settings()));
    }

    [TestMethod]
    public void ReportDescribesEveryStepWithItsCodeName()
    {
        StepOutcome refused = StepOutcomes.NotAttempted("safe-mode:connect", "Safe mode: no device actions.");
        StepOutcome cr = StepOutcomes.FromConfigRet("cm-locate-devnode:X", 0x0D);

        string text = TrayReport.Describe("connect", OpStatus.NotAttempted, "Safe mode: no device actions.", [refused, cr]);

        Assert.AreEqual(
            "connect: NotAttempted. Safe mode: no device actions." +
            " | safe-mode:connect failed NOT_ATTEMPTED (0xA0000001): Safe mode: no device actions." +
            " | cm-locate-devnode:X failed CR_NO_SUCH_DEVNODE (0x0000000D)",
            text);
    }
}
