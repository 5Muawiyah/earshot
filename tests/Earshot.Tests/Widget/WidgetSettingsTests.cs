using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The Widget setting, against the real JsonSettingsStore and its source-generated context.
[TestClass]
public sealed class WidgetSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void TheDefaults()
    {
        WidgetSettings defaults = WidgetSettings.Default;

        Assert.IsTrue(defaults.Enabled);
        Assert.AreEqual("", defaults.OtherDeviceLabel);
        Assert.IsTrue(defaults.AutoPause);
        Assert.IsTrue(defaults.LowBatteryAlert);
        Assert.AreEqual(20, defaults.LowBatteryThresholdPercent);
        Assert.IsTrue(defaults.CaseOpenCard);
        Assert.IsFalse(defaults.LeftClickConnects);
        Assert.AreEqual(WidgetSettings.Default, new EarshotSettings().Widget);
    }

    [TestMethod]
    public void AnOlderFileWithNoWidgetMemberLoadsAsDefault()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\" }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(WidgetSettings.Default, store.Current.Widget);
    }

    [TestMethod]
    public void AWidgetBlockWithAnUnknownMemberLoads()
    {
        File.WriteAllText(
            SettingsPath,
            "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": false, \"SomeFutureMember\": 42 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsFalse(store.Current.Widget.Enabled);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(95)]
    [DataRow(-10)]
    public void ThresholdClampsToTwentyAndIsRecorded(int written)
    {
        var settings = WidgetSettings.Default with { LowBatteryThresholdPercent = written };

        WidgetSettings clamped = settings.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(20, clamped.LowBatteryThresholdPercent);
        Assert.AreEqual(1, notes.Count(n => n.Step == "clamp:LowBatteryThresholdPercent"));
    }

    [TestMethod]
    public void TheLabelIsCleaned()
    {
        var settings = WidgetSettings.Default with { OtherDeviceLabel = "  Sam\u0007's phone " + new string('x', 60) };

        WidgetSettings clamped = settings.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.IsFalse(clamped.OtherDeviceLabel.Contains('\u0007'));
        Assert.AreEqual("Sam's phone " + new string('x', 40 - "Sam's phone ".Length), clamped.OtherDeviceLabel);
        Assert.IsTrue(clamped.OtherDeviceLabel.Length <= 40);
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:OtherDeviceLabel"));
    }

    [TestMethod]
    public void TheMemberNamesAreExactlyThese()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.Widget = s.Widget with { Enabled = false });

        string json = File.ReadAllText(SettingsPath);

        foreach (string member in new[]
                 {
                     "\"Widget\"", "\"Enabled\"", "\"OtherDeviceLabel\"", "\"AutoPause\"", "\"LowBatteryAlert\"",
                     "\"LowBatteryThresholdPercent\"", "\"CaseOpenCard\"", "\"LeftClickConnects\"",
                 })
        {
            Assert.IsTrue(json.Contains(member, StringComparison.Ordinal), member + " is not in the JSON the shipped build wrote: " + json);
        }

        var reread = new JsonSettingsStore(SettingsPath, _log);
        Assert.IsFalse(reread.Current.Widget.Enabled);
    }
}
