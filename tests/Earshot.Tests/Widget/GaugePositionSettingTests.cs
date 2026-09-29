using System.Text.Json;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The "Gauge position" setting: right end by default, next to the apps when chosen, kept in the settings file
// as its number, and a number that names neither is read as the right end.
[TestClass]
public sealed class GaugePositionSettingTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void TheDefaultIsTheRightEnd()
    {
        Assert.AreEqual(GaugePosition.RightEnd, WidgetSettings.Default.GaugePosition);
        Assert.AreEqual(GaugePosition.RightEnd, new EarshotSettings().Widget.GaugePosition);
    }

    [TestMethod]
    public void AnOlderFileWithNoPositionReadsAsTheRightEnd()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": true } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(GaugePosition.RightEnd, store.Current.Widget.GaugePosition);
    }

    [TestMethod]
    public void NextToAppsIsWrittenAndReadBack()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);

        store.Update(s => s.Widget = s.Widget with { GaugePosition = GaugePosition.NextToApps });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.AreEqual(1, document.RootElement.GetProperty("Widget").GetProperty("GaugePosition").GetInt32());
        var reread = new JsonSettingsStore(SettingsPath, _log);
        Assert.AreEqual(GaugePosition.NextToApps, reread.Current.Widget.GaugePosition);
    }

    [TestMethod]
    public void ANumberThatNamesNoPositionIsReadAsTheRightEndAndRecorded()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"GaugePosition\": 7 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(GaugePosition.RightEnd, store.Current.Widget.GaugePosition);
        WidgetSettings clamped = (WidgetSettings.Default with { GaugePosition = (GaugePosition)7 }).Clamped(out IReadOnlyList<Earshot.Contracts.StepOutcome> notes);
        Assert.AreEqual(GaugePosition.RightEnd, clamped.GaugePosition);
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:GaugePosition"));
    }

    // A change is picked up on the next layout: the controller reads the setting every time, so choosing the
    // other position moves the gauge at once rather than at the next restart.
    [TestMethod]
    public void ChangingThePositionMovesTheGaugeOnTheNextLayout()
    {
        var surface = new FakeGaugeSurface();
        var position = GaugePosition.RightEnd;
        using var controller = new GaugeController(
            () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false, position), new CapturingLog(), new Streaming.TestTimeProvider());
        var bar = new System.Drawing.Rectangle(0, 1032, 1920, 48);
        var start = new System.Drawing.Rectangle(762, 1032, 45, 48);
        var layout = new TaskbarLayout(
            0, bar, Earshot.Popup.TaskbarEdge.Bottom, false, new System.Drawing.Rectangle(0, 0, 1920, 1080),
            [start, new System.Drawing.Rectangle(807, 1032, 44, 48), new System.Drawing.Rectangle(1678, 1032, 242, 48)], start,
            96, Earshot.Interop.Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new System.Drawing.Rectangle(1678, 1032, 242, 48));

        controller.OnLayout(ITaskbarReader.Result.Ok(layout));
        var atRight = (GaugeState.Shown)controller.State;
        position = GaugePosition.NextToApps;
        controller.OnLayout(ITaskbarReader.Result.Ok(layout));
        var atApps = (GaugeState.Shown)controller.State;

        Assert.AreEqual(1678 - 8 - 74, atRight.Bounds.Left);
        Assert.AreEqual(851 + 4, atApps.Bounds.Left);
        Assert.AreEqual(1, surface.Calls.Count(c => c.StartsWith("MoveTo", StringComparison.Ordinal)), "One move, no second show.");
    }
}
