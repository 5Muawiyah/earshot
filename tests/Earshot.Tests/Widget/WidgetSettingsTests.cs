using System.Text.Json;
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
        Assert.IsTrue(defaults.ShowOnTaskbar);
        Assert.AreEqual("iPhone", defaults.OtherDeviceLabel);
        Assert.IsTrue(defaults.AutoPause);
        Assert.IsTrue(defaults.LowBatteryAlert);
        Assert.AreEqual(20, defaults.LowBatteryThresholdPercent);
        Assert.IsTrue(defaults.CaseOpenCard);
        Assert.IsFalse(defaults.LeftClickConnects);
        Assert.AreEqual(WidgetSettings.Default, new EarshotSettings().Widget);
    }

    // Enabled - the flag WidgetStatusService itself reads to start or stop the BLE
    // watcher - is the OR of the three consumers, so turning the gauge off alone never stops the watcher
    // another consumer still wants.
    [TestMethod]
    public void WithWatcherRecomputedIsTheOrOfTheThreeConsumers()
    {
        WidgetSettings allOff = WidgetSettings.Default with
        {
            ShowOnTaskbar = false, LowBatteryAlert = false, CaseOpenCard = false, AutoPause = false,
        };
        Assert.IsFalse(allOff.WithWatcherRecomputed().Enabled, "Nothing wants the watcher.");

        Assert.IsTrue((allOff with { ShowOnTaskbar = true }).WithWatcherRecomputed().Enabled);
        Assert.IsTrue((allOff with { LowBatteryAlert = true }).WithWatcherRecomputed().Enabled);
        Assert.IsFalse((allOff with { CaseOpenCard = true }).WithWatcherRecomputed().Enabled, "The lid is not read, so the case-open card is no consumer.");
        Assert.IsTrue((allOff with { AutoPause = true }).WithWatcherRecomputed().Enabled);

        // The gauge going off while another consumer is still on must not turn the watcher off with it.
        WidgetSettings gaugeAndAlert = allOff with { ShowOnTaskbar = true, LowBatteryAlert = true };
        Assert.IsTrue((gaugeAndAlert with { ShowOnTaskbar = false }).WithWatcherRecomputed().Enabled,
            "The low battery alert still wants the watcher even with the gauge off.");
    }

    // Owner's decision: the card reads "On your iPhone" out of the box, not "On another device". The
    // caption shown on the setting itself (WidgetCopy.OtherDeviceCaption) must still say plainly that this
    // is the owner's own label, not something the AirPods report, so a default that reads like a real
    // report never ships without that disclaimer alongside it.
    [TestMethod]
    public void TheDefaultLabelReadsOnYourIPhoneAndTheCaptionStillDisclaimsItIsTheOwnersOwnLabel()
    {
        Assert.AreEqual("On your iPhone", WidgetCopy.OnElsewhere(WidgetSettings.Default.OtherDeviceLabel));
        StringAssert.Contains(WidgetCopy.OtherDeviceCaption, "your own label");
        StringAssert.Contains(WidgetCopy.OtherDeviceCaption, "do not report");
    }

    // The empty-label fallback is unchanged: an owner who clears the default still sees the generic line,
    // never an empty "On your ".
    [TestMethod]
    public void ClearingTheLabelStillShowsOnAnotherDevice()
    {
        Assert.AreEqual("On another device", WidgetCopy.OnElsewhere(""));
        Assert.AreEqual("On another device", WidgetCopy.OnElsewhere("   "));
    }

    [TestMethod]
    public void AnOlderFileWithNoWidgetMemberLoadsAsDefault()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\" }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(WidgetSettings.Default, store.Current.Widget);
        Assert.IsTrue(store.Current.Widget.Enabled, "An older file starts the watcher: Default.Enabled is true.");
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

    // A right-to-left override (U+202E) made to disguise the label's own text, the classic bidi-spoofing
    // shape: char.IsControl alone does not catch it (bidi formatting characters are Unicode category Cf,
    // not Cc), so this needs its own filter.
    [TestMethod]
    public void TheLabelRemovesARightToLeftOverride()
    {
        var settings = WidgetSettings.Default with { OtherDeviceLabel = "Sam‮enohp s'" };

        WidgetSettings clamped = settings.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.IsFalse(clamped.OtherDeviceLabel.Contains('‮'));
        Assert.AreEqual("Samenohp s'", clamped.OtherDeviceLabel);
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:OtherDeviceLabel"));
    }

    // Characters that make a label read as something other than its own text, or as empty, given as code
    // points so the source shows exactly which one each row is (and no invisible character sits in this
    // file). The tag character is outside the Basic Multilingual Plane, a surrogate pair in UTF-16, which a
    // per-char scan cannot see.
    [TestMethod]
    [DataRow(0x200E, "left-to-right mark")]
    [DataRow(0x200F, "right-to-left mark")]
    [DataRow(0x061C, "Arabic letter mark")]
    [DataRow(0x202A, "left-to-right embedding")]
    [DataRow(0x202E, "right-to-left override")]
    [DataRow(0x2066, "left-to-right isolate")]
    [DataRow(0x2069, "pop directional isolate")]
    [DataRow(0x2028, "line separator")]
    [DataRow(0x2029, "paragraph separator")]
    [DataRow(0xFEFF, "byte order mark")]
    [DataRow(0x200B, "zero width space")]
    [DataRow(0x200C, "zero width non-joiner")]
    [DataRow(0x200D, "zero width joiner")]
    [DataRow(0x2060, "word joiner")]
    [DataRow(0x00AD, "soft hyphen")]
    [DataRow(0x0007, "bell")]
    [DataRow(0xE0041, "tag latin capital letter a")]
    public void TheLabelRemovesFormatAndSeparatorCharacters(int codePoint, string name)
    {
        string character = char.ConvertFromUtf32(codePoint);
        var settings = WidgetSettings.Default with { OtherDeviceLabel = "Sam" + character + "'s" + character + " phone" };

        WidgetSettings clamped = settings.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual("Sam's phone", clamped.OtherDeviceLabel, name + " must be removed.");
        Assert.IsTrue(notes.Any(n => n.Step == "clamp:OtherDeviceLabel"), name + " must be recorded as a cleaning.");
    }

    // A label made only of such characters ends up empty, which is a valid choice (the card then says "On
    // another device"), never a blank that reads as a name.
    [TestMethod]
    public void ALabelOfOnlyInvisibleCharactersBecomesEmpty()
    {
        string invisible = char.ConvertFromUtf32(0x200B) + char.ConvertFromUtf32(0x200E) + char.ConvertFromUtf32(0xFEFF) + char.ConvertFromUtf32(0x2028);
        var settings = WidgetSettings.Default with { OtherDeviceLabel = invisible };

        Assert.AreEqual("", settings.Clamped(out _).OtherDeviceLabel);
    }

    // The cap is cut on a text element, never inside a surrogate pair or between a letter and its
    // combining mark. A cut at exactly 40 UTF-16 units would leave half an emoji here.
    [TestMethod]
    public void TheLabelIsNeverCutInsideASurrogatePair()
    {
        string smile = char.ConvertFromUtf32(0x1F600); // two UTF-16 units
        var settings = WidgetSettings.Default with { OtherDeviceLabel = new string('x', 39) + smile + "y" };

        string label = settings.Clamped(out _).OtherDeviceLabel;

        Assert.AreEqual(new string('x', 39), label, "The emoji would straddle the cap, so it is left out whole.");
        AssertNoLoneSurrogate(label);

        // An emoji that fits is kept whole.
        var fits = WidgetSettings.Default with { OtherDeviceLabel = new string('x', 38) + smile + "y" };
        Assert.AreEqual(new string('x', 38) + smile, fits.Clamped(out _).OtherDeviceLabel);
    }

    [TestMethod]
    public void TheLabelIsNeverCutBetweenALetterAndItsCombiningMark()
    {
        string accented = "e" + char.ConvertFromUtf32(0x0301); // one text element, two UTF-16 units
        var settings = WidgetSettings.Default with { OtherDeviceLabel = new string('x', 39) + accented };

        string label = settings.Clamped(out _).OtherDeviceLabel;

        Assert.AreEqual(new string('x', 39), label, "A cut at 40 units would keep the 'e' and drop its accent.");
    }

    [TestMethod]
    public void ALoneSurrogateIsRemovedNotTurnedIntoAReplacementCharacter()
    {
        string lone = ((char)0xD83D).ToString(); // a high surrogate with no low half after it
        var settings = WidgetSettings.Default with { OtherDeviceLabel = "Sam" + lone };

        Assert.AreEqual("Sam", settings.Clamped(out _).OtherDeviceLabel);
    }

    [TestMethod]
    public void CleaningALabelTwiceChangesNothingTheSecondTime()
    {
        string mark = char.ConvertFromUtf32(0x200E);
        string smile = char.ConvertFromUtf32(0x1F600);
        var settings = WidgetSettings.Default with { OtherDeviceLabel = "  " + mark + "A" + char.ConvertFromUtf32(0x0301) + new string('y', 50) + smile + "  " };

        WidgetSettings once = settings.Clamped(out _);
        WidgetSettings twice = once.Clamped(out IReadOnlyList<StepOutcome> secondNotes);

        Assert.AreEqual(once.OtherDeviceLabel, twice.OtherDeviceLabel);
        Assert.IsFalse(secondNotes.Any(n => n.Step == "clamp:OtherDeviceLabel"), "A clean label must not be recorded as cleaned again.");
        Assert.IsTrue(once.OtherDeviceLabel.Length <= WidgetSettings.MaxOtherDeviceLabelLength);
    }

    private static void AssertNoLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.IsTrue(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "A high surrogate at " + i + " has no low half.");
                i++;
            }
            else
            {
                Assert.IsFalse(char.IsLowSurrogate(text[i]), "A low surrogate at " + i + " has no high half.");
            }
        }
    }

    // WidgetSettings.Clamped had no caller in production before this fix: a settings.json a stale build or
    // a hand edit left with a control character, a bidi override, an over-length label or an off-list
    // threshold loaded exactly as written and reached the card unchanged.
    [TestMethod]
    public void LoadingClampsTheWidgetBlock()
    {
        File.WriteAllText(
            SettingsPath,
            "{ \"SchemaVersion\": 1, \"Widget\": { \"OtherDeviceLabel\": \"Sam\\u0007's phone\", \"LowBatteryThresholdPercent\": 37 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual("Sam's phone", store.Current.Widget.OtherDeviceLabel);
        Assert.AreEqual(WidgetSettings.DefaultLowBatteryThresholdPercent, store.Current.Widget.LowBatteryThresholdPercent);
    }

    // The clamp runs on every write too, so a bad value never reaches the file a caller's own mutate
    // delegate did not think to clean up (OtherDeviceNameForm, the only production writer of the label,
    // does its own cleanup on the form itself, but the store must not depend on every future caller doing
    // the same).
    [TestMethod]
    public void SavingClampsTheWidgetBlockAndThatIsWhatIsPersisted()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);

        store.Update(s => s.Widget = s.Widget with { OtherDeviceLabel = "Sam‮'s phone", LowBatteryThresholdPercent = 145 });

        Assert.AreEqual("Sam's phone", store.Current.Widget.OtherDeviceLabel);
        Assert.AreEqual(WidgetSettings.DefaultLowBatteryThresholdPercent, store.Current.Widget.LowBatteryThresholdPercent);

        var reread = new JsonSettingsStore(SettingsPath, _log);
        Assert.AreEqual("Sam's phone", reread.Current.Widget.OtherDeviceLabel, "The clamped value, not the raw one, must be what reaches the file.");
        Assert.AreEqual(WidgetSettings.DefaultLowBatteryThresholdPercent, reread.Current.Widget.LowBatteryThresholdPercent);
    }

    // The prior version only checked that each expected member's name appeared somewhere in the file
    // (Assert.IsTrue(json.Contains(...))), so it could not have failed had the Widget block also carried
    // some other, unwanted member (an address, a tag, anything device-derived) alongside the expected ones.
    // Parses the file and compares the Widget object's own property names as a set, not a substring search.
    [TestMethod]
    public void TheMemberNamesAreExactlyThese()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.Widget = s.Widget with { Enabled = false });

        string json = File.ReadAllText(SettingsPath);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement widget = document.RootElement.GetProperty("Widget");
        var actualMembers = widget.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var expectedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Enabled", "ShowOnTaskbar", "OtherDeviceLabel", "AutoPause", "LowBatteryAlert",
            "LowBatteryThresholdPercent", "CaseOpenCard", "LeftClickConnects", "GaugePosition", "GaugeDisplay",
        };

        CollectionAssert.AreEquivalent(
            expectedMembers.ToList(), actualMembers.ToList(),
            "The Widget block's members must be exactly the documented set: " + json);

        var reread = new JsonSettingsStore(SettingsPath, _log);
        Assert.IsFalse(reread.Current.Widget.Enabled);
    }
}
