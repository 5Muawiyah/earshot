using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Voice;

// v1.1: the owner's spoken status setting, nested in EarshotSettings as VoiceOver and persisted with it
// (Earshot.Infra.SettingsJsonContext already reaches this type through EarshotSettings, the same way it
// already reaches Earshot.Hotkeys.HotkeySettings, so it needs no source-generation attribute of its own).
//
// Default: switched off. A tray utility must not start talking by itself, so Enabled defaults to false
// and every other member is a design choice, not a measurement: no comment on this type may claim
// otherwise. A record, so two settings can be compared for equality with ==, which TrayContext uses to
// decide whether a settings change actually asks for anything different before it reopens the engine.
//
// The members have setters, not init accessors, and that is load-bearing. The source-generated reader builds a
// type with init-only members through one initialiser that sets every member, so a member the file leaves out
// comes back as default(T), not as the default written here: a hand-written VoiceOver block holding only Enabled
// read SpeakFailures as false, Volume as 0 and the three limits as 0, which is speech switched on at no volume.
// With setters it builds the object first and sets only what the file holds. The same reason
// Earshot.Streaming.StreamingSettings gives; VoiceOverSettingsLoadTests pins it.
public sealed record VoiceOverSettings
{
    public bool Enabled { get; set; }

    public bool SpeakFailures { get; set; } = true;

    // Null means the system default voice. A non-null value goes to ISpeechEngine.Open, which passes it
    // to SpeechSynthesizer.SelectVoice (case-sensitive substring match); if nothing matches, the engine
    // records that and falls back to the default voice rather than failing Open.
    public string? VoiceName { get; set; }

    public int Rate { get; set; }

    public int Volume { get; set; } = 100;

    public int RepeatGapMilliseconds { get; set; } = 2000;

    public int ShutdownWaitMilliseconds { get; set; } = 1500;

    public int FailuresBeforeGivingUp { get; set; } = 3;

    public static VoiceOverSettings Default => new();

    // Clamps every numeric member to the range SpeechSynthesizer documents (Rate, Volume) or this
    // feature's own design choice (the rest), and records one note per member actually changed. Clamping
    // is recorded, never applied silently: notes is empty only when nothing needed to move.
    public VoiceOverSettings Clamped(out IReadOnlyList<StepOutcome> notes)
    {
        var list = new List<StepOutcome>();
        VoiceOverSettings result = this with
        {
            Rate = Clamp(Rate, -10, 10, "rate", list),
            Volume = Clamp(Volume, 0, 100, "volume", list),
            RepeatGapMilliseconds = Clamp(RepeatGapMilliseconds, 0, 60000, "repeatGapMilliseconds", list),
            ShutdownWaitMilliseconds = Clamp(ShutdownWaitMilliseconds, 100, 10000, "shutdownWaitMilliseconds", list),
            FailuresBeforeGivingUp = Clamp(FailuresBeforeGivingUp, 1, 10, "failuresBeforeGivingUp", list),
        };

        notes = list;
        return result;
    }

    private static int Clamp(int value, int min, int max, string name, List<StepOutcome> notes)
    {
        int clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            notes.Add(new StepOutcome(
                "clamp:" + name,
                Ok: true,
                Code: 0,
                CodeName: "S_OK",
                Detail: value.ToString(CultureInfo.InvariantCulture) + " clamped to " + clamped.ToString(CultureInfo.InvariantCulture) + "."));
        }

        return clamped;
    }
}
