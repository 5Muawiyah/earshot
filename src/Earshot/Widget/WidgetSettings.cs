using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Widget;

// v1.1: the AirPods widget's data side. Nested in EarshotSettings as Widget and persisted with it through
// Earshot.Infra.SettingsJsonContext, which reaches it through EarshotSettings the way it reaches Streaming.
// An older settings file with no Widget member reads as Default, so nothing about the widget switches on
// by itself for a file an earlier build saved.
//
// Setters, not init accessors, for the reason StreamingSettings records: the source-generated reader
// builds a type with init-only members through one initialiser that sets every member, so a member the
// file leaves out comes back as default(T), not as the default written here.
public sealed record WidgetSettings
{
    public const int DefaultLowBatteryThresholdPercent = 20;
    public const int MaxOtherDeviceLabelLength = 40;

    public bool Enabled { get; set; } = true;                 // the watcher and everything after it

    public string OtherDeviceLabel { get; set; } = "";        // the owner's label; "" shows "On another device"

    public bool AutoPause { get; set; } = true;                // acts only once the gate constant is true

    public bool LowBatteryAlert { get; set; } = true;          // an addition: an off switch beside the threshold

    public int LowBatteryThresholdPercent { get; set; } = DefaultLowBatteryThresholdPercent; // 10 to 90 in steps of 10

    public bool CaseOpenCard { get; set; } = true;

    public bool LeftClickConnects { get; set; }                // false: a left click opens the card

    public static WidgetSettings Default => new();

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
