using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tray;
using Earshot.Update;

namespace Earshot;

// probe setup-values --out <file>
//
// The three values a first install needs and the install script would otherwise have to ask for (the signed-in user's SID,
// the AirPods' address and their container), and what is installed now. Read-only: it reads the settings file, the
// machine folder's device file (only when that folder passes the same permission check the gate applies), the paired
// Bluetooth devices and the installed program's folder, and changes nothing but its own log and the file --out names. It
// never shows a window, takes no elevation and is allowed in safe mode. The report is always JSON, whatever --json says, so
// the script can read the file it names.
//
// Which device is chosen is SetupValues.Select: the first of the machine's device file, the settings' pin, or exactly one
// paired device named like the match string; each of the first two only when it still names a paired device of that address
// and container.
internal static partial class Program
{
    internal const string ProbeSetupValuesTarget = "setup-values";

    static partial void ProbeSetupValues(ProbeContext ctx)
    {
        ctx.Handled = true;
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);

        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value ?? "";
        }

        if (!Sddl.IsUserSid(sid))
        {
            // Without the user's SID there is nothing install could be given; the report says so and the exit code agrees.
            log.Error("probe setup-values: the signed-in user's SID could not be read.");
            sid = "";
            ctx.ExitCode = ExitCodes.OsError;
        }

        var folders = new NtfsFolderSecurity();
        DeviceIdentity? machine = SetupValues.ReadMachineIdentity(paths, folders, log);
        EarshotSettings settings = new JsonSettingsStore(paths.SettingsFile, log, readOnly: true).Current;
        PairedDeviceList paired = new BluetoothDeviceList(log).Read();
        SetupDeviceChoice device = SetupValues.Select(machine, settings, paired.Devices, pairedUnreadable: paired.Devices.Count == 0 && paired.Problems.Count > 0);
        SetupInstall install = SetupValues.ReadInstall(paths.InstalledExe, folders);

        ctx.WriteJson(w => SetupValues.Write(w, new SetupValuesReport(sid, device, install)));
    }
}

// Why ready is false: "not-paired" (no device named like the match), "several" (more than one) or "unreadable" (the
// paired devices could not be listed). Source: "machine", "settings" or "paired". Address and ContainerId are empty
// when not ready.
internal sealed record SetupDeviceChoice(bool Ready, string Reason, string Address, Guid ContainerId, string Source);

// State is InstalledCopy's: "nothing", "usable" or "unusable". Problem is why it is unusable, or empty. Version is the
// installed program's release version (major.minor.patch), or empty when it is not there or reports none.
internal sealed record SetupInstall(string State, string Problem, string Version);

internal sealed record SetupValuesReport(string UserSid, SetupDeviceChoice Device, SetupInstall Install);

internal static class SetupValues
{
    public const int Schema = 1;

    public const string ReasonNotPaired = "not-paired";
    public const string ReasonSeveral = "several";
    public const string ReasonUnreadable = "unreadable";

    // The choice, from the three places a device can come from, in this order. The machine's device file and the settings'
    // pin count only while the device they name is still paired with that address in that container: a pin that
    // outlived its device is not a device to install for. Several devices named like the match is a question for the
    // person, not a guess.
    public static SetupDeviceChoice Select(DeviceIdentity? machine, EarshotSettings settings, IReadOnlyList<PairedDevice> paired, bool pairedUnreadable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paired);

        if (machine is not null && GateStore.ValidateDevice(machine) is null && IsPaired(paired, machine.Address, machine.ContainerId))
        {
            return Chosen(machine.Address, machine.ContainerId, "machine");
        }

        if (BoundaryValidation.IsAddress12(settings.PinnedAddress) && NodeMatch.IsValidTargetContainer(settings.PinnedContainerId) &&
            IsPaired(paired, settings.PinnedAddress, settings.PinnedContainerId))
        {
            return Chosen(settings.PinnedAddress, settings.PinnedContainerId, "settings");
        }

        string match = settings.DeviceMatch;
        PairedDevice[] named = string.IsNullOrWhiteSpace(match)
            ? []
            : paired
                .Where(d => d.Name.Contains(match, StringComparison.OrdinalIgnoreCase) &&
                            BoundaryValidation.IsAddress12(d.Address) && NodeMatch.IsValidTargetContainer(d.ContainerId))
                .GroupBy(d => d.Address, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToArray();

        if (named.Length == 1)
        {
            return Chosen(named[0].Address, named[0].ContainerId, "paired");
        }

        string reason = named.Length > 1 ? ReasonSeveral : pairedUnreadable ? ReasonUnreadable : ReasonNotPaired;
        return new SetupDeviceChoice(false, reason, "", Guid.Empty, "");
    }

    // The device file of the machine folder, only when that folder passes the check the gate makes of it: a file in a
    // folder an ordinary program could have written is not evidence of anything.
    public static DeviceIdentity? ReadMachineIdentity(Paths paths, IFolderSecurity folders, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(log);

        var steps = new List<StepOutcome>();
        if (!FolderTrust.IsTrusted(folders, paths.MachineFolder, AclCheck.CheckMachineFolder, "machine-folder-acl", steps))
        {
            log.Info("probe setup-values: the machine folder was not used (" + string.Join(" ", steps.Where(s => !s.Ok).Select(s => s.Step + " " + s.Detail)) + ").");
            return null;
        }

        GateRead<DeviceIdentity> read = new GateStore(paths.MachineFolder).ReadDevice();
        if (!read.IsOk)
        {
            log.Info("probe setup-values: the device file was not used (" + read.Step.Step + " " + read.Step.CodeName + ": " + read.Step.Detail + ").");
            return null;
        }

        return read.Value;
    }

    public static SetupInstall ReadInstall(string installedExe, IFolderSecurity folders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedExe);
        ArgumentNullException.ThrowIfNull(folders);

        InstallAssessment assessment = InstalledCopy.Assess(installedExe, File.Exists, Directory.Exists, folders);
        string state = assessment.State switch
        {
            InstallState.Usable => "usable",
            InstallState.Unusable => "unusable",
            _ => "nothing",
        };
        string problem = assessment.Problem == InstallProblem.None ? "" : assessment.Problem.ToString();
        return new SetupInstall(state, problem, File.Exists(installedExe) ? ProgramVersion(installedExe) : "");
    }

    // The release version a program file reports, as major.minor.patch, or empty. It is the informational version the
    // release stamps on its own files, without the build metadata after a "+".
    // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.fileversioninfo.productversion
    public static string ProgramVersion(string path)
    {
        string? text;
        try
        {
            text = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (FileNotFoundException)
        {
            return "";
        }

        if (text is null)
        {
            return "";
        }

        int plus = text.IndexOf('+', StringComparison.Ordinal);
        return ReleaseVersion.TryParse(plus >= 0 ? text[..plus] : text, out ReleaseVersion version)
            ? version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture) + "." + version.Patch.ToString(CultureInfo.InvariantCulture)
            : "";
    }

    public static void Write(System.Text.Json.Utf8JsonWriter w, SetupValuesReport report)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(report);
        w.WriteStartObject();
        w.WriteNumber("schema", Schema);
        w.WriteString("userSid", report.UserSid);
        w.WriteBoolean("ready", report.Device.Ready);
        w.WriteString("reason", report.Device.Reason);
        w.WriteString("address", report.Device.Address);
        w.WriteString("containerId", report.Device.ContainerId == Guid.Empty ? "" : report.Device.ContainerId.ToString("D", CultureInfo.InvariantCulture));
        w.WriteString("source", report.Device.Source);
        w.WriteStartObject("install");
        w.WriteString("state", report.Install.State);
        w.WriteString("problem", report.Install.Problem);
        w.WriteString("version", report.Install.Version);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static bool IsPaired(IReadOnlyList<PairedDevice> paired, string address, Guid container) =>
        paired.Any(d => string.Equals(d.Address, address, StringComparison.Ordinal) && d.ContainerId == container);

    private static SetupDeviceChoice Chosen(string address, Guid container, string source) =>
        new(true, "", address, container, source);
}
