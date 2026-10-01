using System.Drawing;
using System.Text.Json;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The "Gauge order" setting: ring, number, bolt by default; kept in the settings file as its number; an older file
// with no member, or a number that names none of the six, reads as the default.
[TestClass]
public sealed class GaugeOrderSettingTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void TheDefaultIsRingNumberBolt()
    {
        Assert.AreEqual(GaugeOrder.RingNumberBolt, WidgetSettings.Default.GaugeOrder);
        Assert.AreEqual(GaugeOrder.RingNumberBolt, new EarshotSettings().Widget.GaugeOrder);
    }

    [TestMethod]
    public void AnOlderFileWithNoOrderReadsAsTheDefault()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": true, \"GaugePosition\": 1 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(GaugeOrder.RingNumberBolt, store.Current.Widget.GaugeOrder);
        Assert.AreEqual(GaugePosition.NextToApps, store.Current.Widget.GaugePosition, "The rest of the file is read as before.");
    }

    [TestMethod]
    public void EachOrderIsWrittenAsItsNumberAndReadBack()
    {
        foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
        {
            File.Delete(SettingsPath);
            var store = new JsonSettingsStore(SettingsPath, _log);
            store.Update(s => s.Widget = s.Widget with { GaugeOrder = order });

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            Assert.AreEqual((int)order, document.RootElement.GetProperty("Widget").GetProperty("GaugeOrder").GetInt32());
            Assert.AreEqual(order, new JsonSettingsStore(SettingsPath, _log).Current.Widget.GaugeOrder);
        }
    }

    [TestMethod]
    public void ANumberThatNamesNoOrderIsReadAsTheDefaultAndRecorded()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"GaugeOrder\": 9 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(GaugeOrder.RingNumberBolt, store.Current.Widget.GaugeOrder);
        WidgetSettings clamped = (WidgetSettings.Default with { GaugeOrder = (GaugeOrder)9 }).Clamped(out IReadOnlyList<StepOutcome> notes);
        Assert.AreEqual(GaugeOrder.RingNumberBolt, clamped.GaugeOrder);
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:GaugeOrder"));
    }

    // The window asks for the order each time it paints, so a choice shows on the taskbar at once.
    [TestMethod]
    public void ThePaintedGaugeFollowsTheOrderTheWindowIsGivenEachTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            GaugeOrder order = GaugeOrder.RingNumberBolt;
            var bounds = new Rectangle(50, 50, 74, 40);
            DateTimeOffset now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            WidgetSnapshot snapshot = WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with
            {
                Where = AirPodsWhere.ThisPc,
                Left = new PartReading(70, false, null) { ReadAt = now },
                Right = new PartReading(60, false, null) { ReadAt = now },
            };
            using var window = new GaugeWindow(_log, new FixedAccent(), () => order);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(window.ShowAt(bounds).Ok);

            window.Render(snapshot, now, GaugeDisplaySettings.Default, 96, bounds, Color.Black, "Segoe UI");
            int before = window.PushCount;

            window.Render(snapshot, now, GaugeDisplaySettings.Default, 96, bounds, Color.Black, "Segoe UI");
            Assert.AreEqual(before, window.PushCount, "Nothing changed, nothing pushed again.");

            order = GaugeOrder.BoltNumberRing;
            window.Render(snapshot, now, GaugeDisplaySettings.Default, 96, bounds, Color.Black, "Segoe UI");
            Assert.AreEqual(before + 1, window.PushCount, "A new order is drawn at once.");
        });
    }

    private sealed class FixedAccent : IAccentColours
    {
        public Color AccentFor(bool lightTheme) => Color.SeaGreen;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
