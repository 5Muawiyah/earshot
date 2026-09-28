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
    public const int MaxOtherDeviceLabelLength = 40;

    // The data pipeline: the BLE watcher, and everything that reads from it (the gauge, the card, the low
    // battery alert, the case-open card, auto-pause). Not written directly by the "Show on the taskbar" menu
    // item any more: it is the OR of ShowOnTaskbar and the other three consumer settings below, kept in sync
    // by WithWatcherRecomputed wherever any of them is written, so
    // turning the gauge off while the low battery alert, the case-open card or auto-pause is still wanted
    // never stops the watcher those three depend on. An older settings file with no Widget member reads as
    // Default, and Default.Enabled is true: the watcher starts for a file an earlier build saved, exactly as
    // it would for a settings file this build wrote itself.
    public bool Enabled { get; set; } = true;

    // The gauge and its card specifically: whether the taskbar (or tray icon fallback) shows anything at
    // all. Independent of Enabled above, which the low battery alert, the case-open card and auto-pause can
    // each keep true on their own even while this is off.
    public bool ShowOnTaskbar { get; set; } = true;

    // The owner's own label for "in use, not on this PC" (WidgetCopy.OtherDeviceCaption says so on the
    // setting itself): defaults to "iPhone" so the card reads "On your iPhone" out of the box (owner's
    // decision), not something the AirPods themselves ever report. "" still shows "On another device"
    // (WidgetCopy.OnElsewhere) for an owner who clears it.
    public string OtherDeviceLabel { get; set; } = "iPhone";

    public bool AutoPause { get; set; } = true;                // acts only once the gate constant is true

    public bool LowBatteryAlert { get; set; } = true;          // an addition: an off switch beside the threshold

    public int LowBatteryThresholdPercent { get; set; } = DefaultLowBatteryThresholdPercent; // 10 to 90 in steps of 10

    public bool CaseOpenCard { get; set; } = true;

    public bool LeftClickConnects { get; set; }                // false: a left click opens the card

    public static WidgetSettings Default => new();

    // Recomputes Enabled from the four consumers (ShowOnTaskbar, LowBatteryAlert, CaseOpenCard, AutoPause):
    // called after any write to one of them, so Enabled - the flag the watcher itself reads - always tells
    // the truth about whether something still needs it, never just mirroring whichever one was last touched.
    public WidgetSettings WithWatcherRecomputed() =>
        this with { Enabled = ShowOnTaskbar || LowBatteryAlert || CaseOpenCard || AutoPause };

    // A threshold that is not a multiple of 10 or is outside 10 to 90 becomes the default and is recorded;
    // the label is trimmed, has its control characters removed, is cut at 40 characters, and is recorded
    // when any of that changed it. Nothing here makes JsonSettingsStore.Validate fail.
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

        WidgetSettings result = this with { LowBatteryThresholdPercent = threshold, OtherDeviceLabel = label };
        notes = list;
        return result;
    }

    private static string CleanLabel(string label, List<StepOutcome> notes)
    {
        string original = label ?? "";
        var builder = new StringBuilder(original.Length);
        foreach (char c in original)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length > MaxOtherDeviceLabelLength)
        {
            cleaned = cleaned[..MaxOtherDeviceLabelLength];
        }

        if (!string.Equals(cleaned, original, StringComparison.Ordinal))
        {
            notes.Add(new StepOutcome("clamp:OtherDeviceLabel", Ok: true, Code: 0, CodeName: "S_OK", Detail: "the label was cleaned."));
        }

        return cleaned;
    }
}
