using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Widget;

// v1.1: the AirPods widget's data side. Nested in EarshotSettings as Widget and persisted with it through
// Earshot.Infra.SettingsJsonContext, which reaches it through EarshotSettings the way it reaches Streaming.
// An older settings file with no Widget member reads as Default, and Default.Enabled is true: the watcher
// starts for a file an earlier build saved, exactly as it would for a settings file this build wrote itself.
//
// Setters, not init accessors, for the reason StreamingSettings records: the source-generated reader
// builds a type with init-only members through one initialiser that sets every member, so a member the
// file leaves out comes back as default(T), not as the default written here.
public sealed record WidgetSettings
{
    public const int DefaultLowBatteryThresholdPercent = 20;
    // A design choice, not a platform limit: 40 UTF-16 units keeps the card's "On your <label>" line and the
    // gauge tooltip to one short line. It is counted in the unit TextBox.MaxLength counts and cut on a text
    // element boundary (CleanedLabel), so a longer label is shortened, never split mid-character.
    public const int MaxOtherDeviceLabelLength = 40;

    // The data pipeline: the BLE watcher, and everything that reads from it (the gauge, the card, the low
    // battery alert, the fully charged notice, the case-open card, auto-pause). Not written directly by the "Show on the taskbar" menu
    // item any more: it is the OR of ShowOnTaskbar and the other four consumer settings below, kept in sync
    // by WithWatcherRecomputed wherever any of them is written, so
    // turning the gauge off while the low battery alert, the fully charged notice, the case-open card or auto-pause is still wanted
    // never stops the watcher those depend on. An older settings file with no Widget member reads as
    // Default, and Default.Enabled is true: the watcher starts for a file an earlier build saved, exactly as
    // it would for a settings file this build wrote itself.
    public bool Enabled { get; set; } = true;

    // The gauge and its card specifically: whether the taskbar (or tray icon fallback) shows anything at
    // all. Independent of Enabled above, which the low battery alert and auto-pause can each keep true on their
    // own even while this is off.
    public bool ShowOnTaskbar { get; set; } = true;

    // The owner's own label for "in use, not on this PC" (WidgetCopy.OtherDeviceCaption says so on the
    // setting itself): defaults to "iPhone" so the card reads "On your iPhone" out of the box (owner's
    // decision), not something the AirPods themselves ever report. "" still shows "On another device"
    // (WidgetCopy.OnElsewhere) for an owner who clears it.
    public string OtherDeviceLabel { get; set; } = "iPhone";

    public bool AutoPause { get; set; } = true;                // acts only once the in-ear signal is known

    public bool LowBatteryAlert { get; set; } = true;          // an addition: an off switch beside the threshold

    // One notice when a part (left, right, case) reaches 100, live or estimated: on by default, for a new install and an
    // existing one alike, so a file that never held it reads as on.
    public bool FullyChargedNotice { get; set; } = true;

    public int LowBatteryThresholdPercent { get; set; } = DefaultLowBatteryThresholdPercent; // 10 to 90 in steps of 10

    // No longer read. Up to v1.3 the case-open card could never show (nothing raised it) and its menu item and settings row
    // were hidden, so a value saved here, on or off, was never the owner's choice of the card that v1.4 shows. Kept as a
    // member so an older settings file still loads and what it held is written back unchanged; CaseOpenCardOn below is
    // what is read now.
    public bool CaseOpenCard { get; set; } = true;

    // The case-open card (CaseOpenCardPresenter): on by default, for a new install and an existing one alike (the owner's
    // decision of 2 October 2026). A new member rather than the old one above, so a file that never held it, which is every
    // file written before v1.4, reads as on, whatever the old member says.
    public bool CaseOpenCardOn { get; set; } = true;

    // When the case-open card closes by itself: 0 for when the case closes (the default), or after 5, 10, 30 or 60
    // seconds. The case closing, the card's close button and a click on its Connect button close it whatever this says.
    public int CaseOpenCardCloseSeconds { get; set; } = CaseOpenCardClose.UntilCaseCloses;

    // Which displays show the case-open card: empty for where the gauge is (the default), CaseOpenCardDisplays.All alone
    // for every display, or the stored identities of the chosen displays (DisplayInfo.Id, the monitor's device interface
    // path, as GaugeDisplay stores one).
    //
    // An empty list is always the one shared empty array, however it was made (a file read gives a new one): a record
    // compares an array by reference, so this keeps a setting read back at its default equal to the default.
    public string[] CaseOpenCardDisplays
    {
        get => _caseOpenCardDisplays;
        set => _caseOpenCardDisplays = value is { Length: 0 } ? [] : value;
    }

    private string[] _caseOpenCardDisplays = [];

    public bool LeftClickConnects { get; set; }                // false: a left click opens the card

    // Where the gauge sits on the taskbar: the right-hand end (the default) or next to the app buttons.
    // Written as its number; a number that names neither position is read as RightEnd (Clamped records it).
    public GaugePosition GaugePosition { get; set; } = GaugePosition.RightEnd;

    // How the gauge's ring, number and charging bolt line up. Written as its number; a number that names none of the
    // six orders is read as RingNumberBolt, the layout the gauge has always had (Clamped records it).
    public GaugeOrder GaugeOrder { get; set; } = GaugeOrder.RingNumberBolt;

    // Which display's taskbar holds the gauge: "" for the main display (the default), or the stored identity of one
    // display (DisplayInfo.Id, the monitor's device interface path, not its place in a list). A display that is not
    // connected, or shows no taskbar, leaves the gauge on the main display's taskbar until it is back.
    public string GaugeDisplay { get; set; } = "";

    // The longest identity kept: a device interface path is well under this, so a longer value is not one.
    public const int MaxGaugeDisplayLength = 512;

    public static WidgetSettings Default => new();

    // Recomputes Enabled from the five consumers (ShowOnTaskbar, LowBatteryAlert, AutoPause, CaseOpenCardOn, FullyChargedNotice):
    // called after any write to one of them, so Enabled - the flag the watcher itself reads - always tells
    // the truth about whether something still needs it, never just mirroring whichever one was last touched.
    public WidgetSettings WithWatcherRecomputed() =>
        this with { Enabled = ShowOnTaskbar || LowBatteryAlert || AutoPause || CaseOpenCardOn || FullyChargedNotice };

    // A threshold that is not a multiple of 10 or is outside 10 to 90 (the same list the menu itself
    // offers) becomes the default and is recorded; the label has its control, format and separator
    // characters removed, is trimmed, is cut at MaxOtherDeviceLabelLength, and is recorded when any of that
    // changed it. Nothing here makes JsonSettingsStore.Validate fail: a bad value is corrected in place,
    // never rejected outright, since a settings write must never fail just because the label field carried
    // something odd.
    public WidgetSettings Clamped(out IReadOnlyList<StepOutcome> notes)
    {
        var list = new List<StepOutcome>();

        int threshold = LowBatteryThresholdPercent;
        if (threshold is < 10 or > 90 || threshold % 10 != 0)
        {
            list.Add(new StepOutcome(
                "clamp:LowBatteryThresholdPercent",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: threshold.ToString(CultureInfo.InvariantCulture) + " is not a multiple of 10 from 10 to 90, so " +
                    DefaultLowBatteryThresholdPercent.ToString(CultureInfo.InvariantCulture) + " is used."));
            threshold = DefaultLowBatteryThresholdPercent;
        }

        string label = CleanLabel(OtherDeviceLabel, list);

        GaugePosition position = GaugePosition;
        if (!Enum.IsDefined(position))
        {
            list.Add(new StepOutcome(
                "clamp:GaugePosition",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: ((int)position).ToString(CultureInfo.InvariantCulture) + " is not a gauge position, so the right-hand end is used."));
            position = GaugePosition.RightEnd;
        }

        GaugeOrder order = GaugeOrder;
        if (!Enum.IsDefined(order))
        {
            list.Add(new StepOutcome(
                "clamp:GaugeOrder",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: ((int)order).ToString(CultureInfo.InvariantCulture) + " is not one of the six gauge orders, so ring, number, bolt is used."));
            order = GaugeOrder.RingNumberBolt;
        }

        string display = (GaugeDisplay ?? "").Trim();
        if (display.Length > MaxGaugeDisplayLength || display.Any(char.IsControl))
        {
            list.Add(new StepOutcome(
                "clamp:GaugeDisplay",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: "the gauge display is not an identity Windows gives, so the main display is used."));
            display = "";
        }

        int closeSeconds = CaseOpenCardCloseSeconds;
        if (!CaseOpenCardClose.IsChoice(closeSeconds))
        {
            list.Add(new StepOutcome(
                "clamp:CaseOpenCardCloseSeconds",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: closeSeconds.ToString(CultureInfo.InvariantCulture) + " is not one of the case-open card's close choices, so it closes when the case closes."));
            closeSeconds = CaseOpenCardClose.UntilCaseCloses;
        }

        string[] cardDisplays = CaseOpenCardDisplayChoice.Cleaned(CaseOpenCardDisplays, out bool displaysChanged);
        if (displaysChanged)
        {
            list.Add(new StepOutcome(
                "clamp:CaseOpenCardDisplays",
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: "the case-open card's displays held a value that is not a display identity, so it was left out."));
        }

        WidgetSettings result = this with
        {
            LowBatteryThresholdPercent = threshold, OtherDeviceLabel = label, GaugePosition = position, GaugeOrder = order, GaugeDisplay = display,
            CaseOpenCardCloseSeconds = closeSeconds, CaseOpenCardDisplays = cardDisplays,
        };
        notes = list;
        return result;
    }

    // Whether a character may stay in the label. Control characters (Cc), and every character in the
    // Unicode categories Format (Cf), Line separator (Zl) and Paragraph separator (Zp) go. Cf is what
    // holds the marks that make a label read as something other than its own text or as empty: the
    // bidirectional marks and overrides (U+200E, U+200F, U+061C, U+202A-U+202E, U+2066-U+2069), the
    // zero-width characters (U+200B-U+200D, U+2060), the byte order mark (U+FEFF) and the tag characters.
    // Zl and Zp are U+2028 and U+2029. Tested on the whole code point, so a Format character outside the
    // Basic Multilingual Plane is caught too.
    // https://www.unicode.org/reports/tr9/#Explicit_Directional_Formatting_Characters
    // https://www.unicode.org/reports/tr44/#General_Category_Values
    private static bool IsRemovedFromLabel(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    // The label as the card may show it. Also what the name form returns, so the form and the store agree.
    internal static string CleanedLabel(string? label)
    {
        string text = label ?? "";
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            Rune rune;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                rune = new Rune(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                continue; // a lone half of a pair is not a character
            }
            else
            {
                rune = new Rune(text[i]);
            }

            if (!IsRemovedFromLabel(rune))
            {
                builder.Append(rune.ToString());
            }
        }

        return CutAtTextElement(builder.ToString().Trim(), MaxOtherDeviceLabelLength);
    }

    // Keeps whole text elements (a letter with its combining marks, an emoji sequence, a surrogate pair)
    // while the total stays within maxUtf16Units, so nothing is ever cut in the middle of one. Counted in
    // UTF-16 units, the unit TextBox.MaxLength counts, so the name form's own cap and this one agree. An
    // element that alone is longer than the cap is left out whole.
    private static string CutAtTextElement(string text, int maxUtf16Units)
    {
        if (text.Length <= maxUtf16Units)
        {
            return text;
        }

        int kept = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (kept + element.Length > maxUtf16Units)
            {
                break;
            }

            kept += element.Length;
        }

        return text[..kept].TrimEnd();
    }

    private static string CleanLabel(string label, List<StepOutcome> notes)
    {
        string original = label ?? "";
        string cleaned = CleanedLabel(original);

        if (!string.Equals(cleaned, original, StringComparison.Ordinal))
        {
            notes.Add(new StepOutcome("clamp:OtherDeviceLabel", Ok: true, Code: 0, CodeName: "S_OK", Detail: "the label was cleaned."));
        }

        return cleaned;
    }
}
