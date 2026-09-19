using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Streaming;

// v1.1: the owner's Play from a phone setting, nested in EarshotSettings as Streaming and persisted with it
// (Earshot.Infra.SettingsJsonContext reaches this type through EarshotSettings, the way it reaches
// HotkeySettings and VoiceOverSettings, so it needs no source-generation attribute of its own). The file
// holds these member names exactly as written here, in PascalCase; a camelCase block is read as unknown
// members and ignored.
//
// Default: switched off. While Enabled is false the menu item does not appear, nothing is enumerated, no
// WinRT type is touched and nothing is listening. The two limits are design choices, not measurements.
//
// The members have setters, not init accessors, and that is load-bearing. The source-generated reader builds a type
// with init-only members through one initialiser that sets every member, so a member the file leaves out comes back
// as default(T), not as the default written here: a Streaming block holding only Enabled read LastDeviceKey as null
// (and the store then threw while copying the settings) and both limits as zero. With setters it builds the object
// first and sets only what the file holds. StreamingSettingsTests pins it.
//
// There is no AskBeforeStopping member. The workshop design reserved one for a confirmation style Earshot does
// not have, and a saved setting that changes nothing is a promise nobody keeps.
public sealed record StreamingSettings
{
    public const int DefaultOpenTimeoutSeconds = 20;
    public const int DefaultDiscoveryTimeoutSeconds = 5;

    public bool Enabled { get; set; }

    // A short hash of the device last played from (StreamingLog.Key), so the menu can show it first. Never the
    // id itself, which carries hardware identifiers, and never displayed. Cleared once a good read of the
    // list no longer holds that device.
    public string LastDeviceKey { get; set; } = "";

    // Earshot's own limit on OpenAsync.
    public int OpenTimeoutSeconds { get; set; } = DefaultOpenTimeoutSeconds;

    // Earshot's own limit on reading the list.
    public int DiscoveryTimeoutSeconds { get; set; } = DefaultDiscoveryTimeoutSeconds;

    public static StreamingSettings Default => new();

    // A value outside its range is replaced by the default, not pulled to the nearest end: a limit of zero or
    // of hours was never meant, so the nearest end is no better a guess than the default. Every replacement is
    // recorded; notes is empty only when nothing needed to move.
    public StreamingSettings Clamped(out IReadOnlyList<StepOutcome> notes)
    {
        var list = new List<StepOutcome>();
        StreamingSettings result = this with
        {
            OpenTimeoutSeconds = InRangeOrDefault(OpenTimeoutSeconds, 5, 120, DefaultOpenTimeoutSeconds, nameof(OpenTimeoutSeconds), list),
            DiscoveryTimeoutSeconds = InRangeOrDefault(DiscoveryTimeoutSeconds, 2, 30, DefaultDiscoveryTimeoutSeconds, nameof(DiscoveryTimeoutSeconds), list),
        };

        notes = list;
        return result;
    }

    private static int InRangeOrDefault(int value, int min, int max, int fallback, string name, List<StepOutcome> notes)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        notes.Add(new StepOutcome(
            "clamp:" + name,
            Ok: true,
            Code: 0,
            CodeName: "S_OK",
            Detail: value.ToString(CultureInfo.InvariantCulture) + " is outside " + min.ToString(CultureInfo.InvariantCulture) + " to " +
                max.ToString(CultureInfo.InvariantCulture) + ", so " + fallback.ToString(CultureInfo.InvariantCulture) + " is used."));
        return fallback;
    }
}
