using System.Drawing;
using Earshot.Bluetooth;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The full card's states for the PC's own Bluetooth and for a PC with no AirPods paired: what the status row says, which controls
// work, what the button says and does. They show only on a card the person asked for.
[TestClass]
public sealed class CardRadioStateTests
{
    [TestMethod]
    [DataRow("Ok", "Ok")]
    [DataRow("Off", "Off")]
    [DataRow("SwitchedOff", "Off")]
    [DataRow("Problem", "NotWorking")]
    [DataRow("NoAdapter", "NotWorking")]
    [DataRow("NoClassic", "NotWorking")]
    public void ARadioCheckResultMapsToTheCardsRadioState(string state, string expected)
    {
        Assert.AreEqual(Enum.Parse<CardRadio>(expected), CardRadioMap.From(Enum.Parse<RadioCheckState>(state)));
    }

    [TestMethod]
    [DataRow("Unreachable")]
    [DataRow("Unknown")]
    public void AResultThatSaysNothingAboutTheRadioMapsToNothing(string state)
    {
        Assert.IsNull(CardRadioMap.FromOrNull(Enum.Parse<RadioCheckState>(state)));
    }

    [TestMethod]
    public void BluetoothNotWorkingShowsTheErrorRowDisablesRefreshAndOffersBluetoothSettings()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            int settings = 0;
            int toggles = 0;
            card.BluetoothSettingsRequested += (_, _) => settings++;
            card.ToggleRequested += (_, _) => toggles++;
            card.Render(CardKit.MainModel() with { Radio = CardRadio.NotWorking }, 96);

            Assert.AreEqual(FluentGlyphs.Error, card.StatusRow.Glyph);
            Assert.AreEqual("Bluetooth isn't working", card.StatusRow.Text);
            Assert.AreEqual(card.CautionInk, card.StatusRow.Ink);
            Assert.IsFalse(card.BluetoothOff, "It is not the off state.");

            CardControl refresh = card.CurrentControls().Single(c => c.Name == WidgetCopy.RefreshBattery);
            Assert.IsFalse(refresh.Enabled, "Refresh is disabled.");
            CardControl status = card.CurrentControls().Single(c => c.Role == CardControlRole.StaticText && c.Name == "Bluetooth isn't working");
            Assert.AreEqual("Bluetooth on this PC isn't working", status.Tooltip);

            CardControl button = card.CurrentControls().Single(c => c.Name == "Bluetooth settings");
            Assert.IsTrue(button.Enabled);
            button.Activate();
            Assert.AreEqual(1, settings);
            Assert.AreEqual(0, toggles, "It does not connect.");
        });
    }

    [TestMethod]
    public void NoAirPodsPairedShowsTheBluetoothRowAndAnAccentPairAirPodsButton()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            int pair = 0;
            int toggles = 0;
            card.PairAirPodsRequested += (_, _) => pair++;
            card.ToggleRequested += (_, _) => toggles++;
            card.Render(CardKit.MainModel() with { Radio = CardRadio.NoAirPodsPaired, ConnectIntent = true }, 96);

            Assert.AreEqual(FluentGlyphs.Bluetooth, card.StatusRow.Glyph);
            Assert.AreEqual("No AirPods paired", card.StatusRow.Text);

            CardControl button = card.CurrentControls().Single(c => c.Name == "Pair AirPods");
            Assert.IsTrue(button.Enabled);
            using Bitmap bitmap = CardKit.Render(card);
            Rectangle b = card.CurrentMainLayout.Button;
            Color face = bitmap.GetPixel(b.X + 6, b.Y + (b.Height / 2));
            card.Render(CardKit.MainModel() with { ConnectIntent = true }, 96);
            using Bitmap connect = CardKit.Render(card);
            Assert.AreEqual(connect.GetPixel(b.X + 6, b.Y + (b.Height / 2)).ToArgb(), face.ToArgb(), "The button is the accent Connect is.");
            card.Render(CardKit.MainModel() with { ConnectIntent = false }, 96);
            using Bitmap disconnect = CardKit.Render(card);
            Assert.AreNotEqual(disconnect.GetPixel(b.X + 6, b.Y + (b.Height / 2)).ToArgb(), face.ToArgb(), "Not the standard control.");
            card.Render(CardKit.MainModel() with { Radio = CardRadio.NoAirPodsPaired, ConnectIntent = true }, 96);

            button.Activate();
            Assert.AreEqual(1, pair);
            Assert.AreEqual(0, toggles, "It does not connect.");
            Assert.IsTrue(card.CurrentControls().Single(c => c.Name == WidgetCopy.RefreshBattery).Enabled, "Refresh still works.");
        });
    }

    [TestMethod]
    public void TheRadioCheckSayingOffGivesTheBluetoothOffRowAndDisablesConnectAndRefresh()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.MainModel() with { Radio = CardRadio.Off }, 96);

            Assert.IsTrue(card.BluetoothOff);
            Assert.AreEqual("Bluetooth off", card.StatusRow.Text);
            Assert.IsFalse(card.ButtonUsable);
            Assert.IsFalse(card.CurrentControls().Single(c => c.Name == WidgetCopy.RefreshBattery).Enabled);
        });
    }

    [TestMethod]
    public void ACardNobodyAskedForShowsNoneOfTheRadioStates()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (CardRadio radio in new[] { CardRadio.Off, CardRadio.NotWorking, CardRadio.NoAirPodsPaired })
            {
                using WidgetCard card = CardKit.NewCard(dark: false, notice: true);
                card.Render(CardKit.MainModel() with { Radio = radio }, 96);
                Assert.IsFalse(card.BluetoothOff, radio.ToString());
                Assert.AreNotEqual("Bluetooth isn't working", card.StatusRow.Text, radio.ToString());
                Assert.AreNotEqual("No AirPods paired", card.StatusRow.Text, radio.ToString());
            }
        });
    }
}
