namespace Earshot.Commands;

// What a doctor run reads from this PC, before anything is made safe to print. A source fills it; the report is built from
// it. A part a source could not read is null here and named in Unreadable with the reason.
internal sealed class DoctorFacts
{
    public InstallFacts? Install { get; set; }

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

internal sealed class RadioFact
{
    public bool Found { get; set; }

    public string? DriverVersion { get; set; }
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
