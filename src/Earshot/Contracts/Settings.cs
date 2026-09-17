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
}

// SYSTEM-owned, %ProgramData%\Earshot\config.json. Users-read, SYSTEM-write via setboot verbs only.
public sealed class GateConfig
{
    public int  SchemaVersion { get; set; } = 1;
    public bool BlockAtBoot   { get; set; } = true;   // owner: default ON. Canonical copy.
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
