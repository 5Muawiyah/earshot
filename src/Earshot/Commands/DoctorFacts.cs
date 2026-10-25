namespace Earshot.Commands;

// What a doctor run reads from this PC, before anything is made safe to print. A source fills it; the report is built from
// it. A part a source could not read is null here and named in Unreadable with the reason.
internal sealed class DoctorFacts
{
    public InstallFacts? Install { get; set; }

    public StartupFact? Startup { get; set; }

    public int? TrayProcesses { get; set; }

    public List<TaskFact>? Tasks { get; set; }

    public ServiceFact? Service { get; set; }

    public List<DeviceFact>? Devices { get; set; }

    public List<BtServiceFact>? BtServices { get; set; }

    public RadioFact? Radio { get; set; }

    public List<FilterFact>? Filters { get; set; }

    public HistoryFact? History { get; set; }

    public bool? LastReadingSaved { get; set; }

    public IdentityFact? Identity { get; set; }

    public ProtectionFact? Protection { get; set; }

    public List<UnreadableFact> Unreadable { get; set; } = [];
}

internal sealed class InstallFacts
{
    public string? Folder { get; set; }

    public bool Installed { get; set; }

    public string? Version { get; set; }

    public bool AppsEntry { get; set; }

    public string? AppsVersion { get; set; }

    public bool StartMenuShortcut { get; set; }

    public bool RunValue { get; set; }
}

internal sealed class TaskFact
{
    public string? Name { get; set; }

    public bool Present { get; set; }

    public string? State { get; set; }
}

internal sealed class ServiceFact
{
    public bool Present { get; set; }

    public string? State { get; set; }
}

// One Bluetooth device node on Apple's vendor id. Name, Serial and Container are read only so the report can take them out of
// any text; the report never carries them.
internal sealed class DeviceFact
{
    public string InstanceId { get; set; } = "";

    public string? Name { get; set; }

    public string? Serial { get; set; }

    public string? Container { get; set; }

    public string? Status { get; set; }

    public bool Present { get; set; }

    public int? Problem { get; set; }

    public int? ConfigFlags { get; set; }
}

internal sealed class BtServiceFact
{
    public string? Name { get; set; }

    public string? State { get; set; }
}

// What the radio check read: the adapter Windows uses, its radio state, its device node's status bits and problem number, and the
// driver's version and date. Everything is a plain code or a number; a part that could not be read is null and its raw code is in
// ReadCodes. Found stays "a radio was read".
internal sealed class RadioFact
{
    public bool Found { get; set; }

    public string? DriverVersion { get; set; }

    public bool? AdapterFound { get; set; }

    public bool? ClassicSupported { get; set; }

    // The radio check's verdict by name: Ok, Off, SwitchedOff, Problem, NoAdapter or Unknown.
    public string? State { get; set; }

    // Windows.Devices.Radios.RadioState by name: Unknown, On, Off or Disabled.
    public string? RadioState { get; set; }

    // DN_* status bits as 0x-prefixed hex.
    public string? DevNodeStatus { get; set; }

    public int? ProblemCode { get; set; }

    // yyyy-MM-dd
    public string? DriverDate { get; set; }

    // "step:code name" of each read, for the ones an AI follows up.
    public List<string>? ReadCodes { get; set; }
}

// The sign-in start of Earshot: the Run value's command, whether the file it names is there, and the first byte of the
// StartupApproved flag as hex (the format is undocumented; the tray reads bit 0x1 as "turned off in Windows").
internal sealed class StartupFact
{
    public string? RunCommand { get; set; }

    public bool? TargetExists { get; set; }

    public string? ApprovedFlag { get; set; }

    // The program file a Run command starts: the quoted path, or an unquoted one up to its first option. Null for no command.
    public static string? TargetOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string text = command.Trim();
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : text[1..];
        }

        int option = text.IndexOf(" --", StringComparison.Ordinal);
        return option > 0 ? text[..option] : text;
    }
}

// One pairing run as the connect history kept it: when, how it ended, the status Windows answered, the model check and how
// long it took. No name, address or id.
internal sealed class PairingFact
{
    public string? Time { get; set; }

    public string? Outcome { get; set; }

    public string? Status { get; set; }

    public long? StatusValue { get; set; }

    // "airpods", "not-airpods", or null when the run ended before the check.
    public string? ModelCheck { get; set; }

    public long? DurationMs { get; set; }
}

internal sealed class FilterFact
{
    public string? Name { get; set; }

    public string? On { get; set; }
}

internal sealed class HistoryFact
{
    public bool Exists { get; set; }

    public int? Lines { get; set; }

    public List<string>? Last { get; set; }

    // The last five pairing runs, newest last.
    public List<PairingFact>? Pairing { get; set; }
}

// Whether Earshot holds a record of the AirPods it protects, and how many paired sets of AirPods are not that one. Counts only, so
// the report carries no name or identifier of any of them.
internal sealed class ProtectionFact
{
    public bool Pinned { get; set; }

    public int Unprotected { get; set; }
}

internal sealed class IdentityFact
{
    public string? User { get; set; }

    public string? Computer { get; set; }

    public string? Profile { get; set; }

    public string? LocalAppData { get; set; }
}

internal sealed class UnreadableFact
{
    public string? Part { get; set; }

    public string? Reason { get; set; }
}

// Where the facts come from. The real one reads this PC without changing it; a test hands in a fake.
internal interface IDoctorSource
{
    DoctorFacts Read();
}
