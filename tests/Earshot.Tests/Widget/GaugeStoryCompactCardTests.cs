using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// Which cards stand in for the gauge's story. A full card tells a toggle itself, so the gauge keeps quiet under it; a compact row
// says nothing of a toggle, so the gauge tells it (a failure with no next step is then said by the cross and nowhere else).
// Proved through the real TrayContext with only the outside replaced. Every window is on a private desktop.
[TestClass]
public sealed class GaugeStoryCompactCardTests
{
    private static Action<EarshotSettings> CardsOn =>
        s => s.Widget = s.Widget with { ShowOnTaskbar = false, CaseOpenCardOn = true, LeftClickConnects = true, Enabled = true };

    [TestMethod]
    public void ACompactRowOnScreenLeavesTheGaugeItsStory()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), settings: CardsOn, cardEnvironmentFactory: () => new Phase5.FakeCardEnvironment());
            tray.PumpUntilIdle();
            Task<bool> shown = tray.Context.ShowCompactForTest(new CompactRequest(CompactContents.FullyCharged(), CardAnchor.NearTray))!;
            tray.PumpUntilIdle();
            Assert.IsTrue(shown.IsCompletedSuccessfully && shown.Result, "Sanity: the compact row is on screen.");
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardIsShownForTest, "Sanity: it is an unasked card. " + string.Join(" | ", tray.Log.Entries.Select(e => e.Message).TakeLast(8)));

            tray.Context.GaugeStoryForTest.ToggleStarted(wanted: true, ToggleOrigin.Elsewhere);

            Assert.AreEqual(GaugeStoryKind.Spinning, tray.Context.GaugeStoryForTest.Current.Kind, "The row tells no toggle, so the gauge does.");
        });
    }

    [TestMethod]
    public void AFullCardOnScreenStillTellsTheToggleItselfAndTheGaugeStaysQuiet()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), settings: CardsOn, cardEnvironmentFactory: () => new Phase5.FakeCardEnvironment());
            tray.PumpUntilIdle();
            tray.Context.RaiseCaseOpenedForTest();
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardIsShownForTest, "Sanity: the case card is open.");

            tray.Context.GaugeStoryForTest.ToggleStarted(wanted: true, ToggleOrigin.Elsewhere);

            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind);
        });
    }

    [TestMethod]
    public void TheRowGrowingToTheFullCardHandsTheStoryToTheCardAndFoldingBackHandsItToTheGauge()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog(), CardOpening.Unasked);
            var seen = new List<bool>();
            card.OnScreenChanged += (_, _) => seen.Add(card.IsCompactShown);
            CompactContent row = new('', InkRole.Primary, "Moved to iPhone", null, TimeSpan.FromSeconds(6));
            WidgetCardModel compact = CardKit.MainModel() with { Form = CardForm.Compact, Compact = row };
            card.Render(compact, 96);
            card.Show();
            Application.DoEvents();
            seen.Clear();

            card.Render(CardKit.MainModel(), 96);
            Assert.IsTrue(seen.Count > 0, "Growing is told, so whoever keeps the gauge's story can look again.");
            Assert.IsFalse(seen[^1], "and it is told with the full card on show.");

            seen.Clear();
            card.Render(compact, 96);
            Assert.IsTrue(seen.Count > 0, "Folding is told too.");
            Assert.IsTrue(seen[^1]);
        });
    }
}
