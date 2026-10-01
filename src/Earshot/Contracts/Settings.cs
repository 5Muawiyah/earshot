namespace Earshot.Contracts;

// User-scoped, non-security-critical, tray-owned. %APPDATA%\Earshot\settings.json.
public sealed class EarshotSettings
{
    public int    SchemaVersion         { get; set; } = 1;
    public string DeviceMatch           { get; set; } = "AirPods";  // configurable; matched OrdinalIgnoreCase
    public bool   ProtectAudioQuality   { get; set; } = true;       // owner: default ON (intent)
    public bool   ProtectAudioNoticeShown { get; set; }             // one-time mic-notice latch
    public bool   OpenOnStartup         { get; set; } = true;       // tray must run to enforce the invariant
    public Guid   PinnedContainerId     { get; set; } = Guid.Empty; // learned once from discovery
    public string PinnedAddress         { get; set; } = "";         // 12 hex uppercase, e.g. "0A1B2C3D4E8C"
    // NOTE: BlockAtBoot is NOT here. Its authority is the SYSTEM-owned GateConfig, because the
    // BootBlock task (SYSTEM, no user session) must read it and cannot read HKCU/%APPDATA%.

    // Hand back the AirPods (release, then block) at shut down, at sleep and on Exit from the menu while they are
    // connected to this PC. Off in the class, so a settings file with no member (one written before this setting
    // existed) reads as off; a file that holds the member keeps its value. A PC with no settings file and no backup, a
    // new install, gets it on from NewInstallDefaults: the owner decided new installs hand back, and an existing
    // install's saved choice is never changed. SchemaVersion stays 1; an older file without this member still reads as
    // this build's current schema, not a newer one.
    public bool   HandBackOnShutdownAndSleep { get; set; }

    // What a PC with no settings file and no backup gets: the initialisers, with Hand back and Open on startup on.
    // Open on startup is already on in the class, and is named here so the two choices that belong to a new install
    // are read in one place.
    public static EarshotSettings NewInstallDefaults() => new() { HandBackOnShutdownAndSleep = true, OpenOnStartup = true };

    // "Check automatically": look for a newer release once a day, after startup. Defaults off, because a check
    // contacts GitHub, so an older settings file with no member reads as off and nothing is contacted until the
    // owner asks. A check never downloads or installs anything; that stays behind the owner's own click.
    public bool   CheckForUpdatesAutomatically { get; set; }

    // Pause this PC's playback when the AirPods stop being this PC's output while it was playing to them
    // (the phone took them, they went out of range, or Earshot let them go). Never resumes on its own. It pauses the
    // one media session playing on this PC whichever output that session uses, since Windows does not say which
    // device a session renders to (Earshot.Widget.EarPause.SessionPause). Defaults
    // on, so an older settings file with no member reads as on. SchemaVersion stays 1.
    public bool   PauseWhenAirPodsLeave { get; set; } = true;

    // v1.1: global keyboard shortcuts. Defaults on with the two directional chords typed, Ctrl+Alt+Shift+A to
    // connect and Ctrl+Alt+Shift+D to disconnect (Earshot.Hotkeys.HotkeySettings), and every other shortcut empty.
    // An older settings file that never typed a chord reads as on with those two, and a chord the owner cleared
    // stays cleared.
    public Earshot.Hotkeys.HotkeySettings Hotkeys { get; set; } = new();

    // v1.1: spoken status. Defaults off (Earshot.Voice.VoiceOverSettings.Default), so an older settings
    // file with no VoiceOver member reads as "off" and nothing is ever spoken until the owner asks.
    public Earshot.Voice.VoiceOverSettings VoiceOver { get; set; } = Earshot.Voice.VoiceOverSettings.Default;

    // v1.1: play from a phone, this PC as a Bluetooth audio sink. Defaults off
    // (Earshot.Streaming.StreamingSettings.Default), so an older settings file with no Streaming member reads
    // as "off": the menu item does not appear, nothing is enumerated and nothing listens until the owner asks.
    public Earshot.Streaming.StreamingSettings Streaming { get; set; } = Earshot.Streaming.StreamingSettings.Default;

    // v1.1: the AirPods widget's data side. Defaults on (Earshot.Widget.WidgetSettings.Default), so an
    // older settings file with no Widget member reads as the widget's own defaults; every feature that
    // depends on the advertisement still fails closed on its own until the owner's battery set-ups have proved what it needs.
    public Earshot.Widget.WidgetSettings Widget { get; set; } = Earshot.Widget.WidgetSettings.Default;
}

// SYSTEM-owned, %ProgramData%\Earshot\config.json. Users-read, SYSTEM-write via setboot verbs only.
public sealed class GateConfig
{
    public int  SchemaVersion { get; set; } = 1;
    public bool BlockAtBoot   { get; set; } = true;   // owner: default ON. Canonical copy.

    // Whether the hand-back service blocks the AirPods at shut down when the tray did not. The service cannot read the
    // user's settings file, so the setting is copied here (sethandback-on and sethandback-off). A file written before
    // the member existed has none, and reads as off, as the tray's own setting does.
    public bool HandBackAtShutdown { get; set; }
}

// SYSTEM-owned, %ProgramData%\Earshot\device.json. The identity the gate trusts.
public sealed class DeviceIdentity
{
    public string Address     { get; set; } = "";     // 12 hex uppercase
    public Guid   ContainerId { get; set; } = Guid.Empty;
}

// SYSTEM-owned, %ProgramData%\Earshot\protection.json. GUIDs Earshot itself disabled, for exact restore.
public sealed class ProtectionRecord
{
    public List<Guid> DisabledServices { get; set; } = new();

    // A protect (true) or restore (false) request waiting to be applied; null when none is pending.
    // Optional in the file: a record written before this member existed reads as null.
    public bool? PendingProtect { get; set; }
}

public interface ISettingsStore
{
    EarshotSettings Current { get; }
    event EventHandler<EarshotSettings>? Changed;
    void Update(Action<EarshotSettings> mutate);   // mutate a copy, persist atomically, raise Changed
    void Reload();
}
