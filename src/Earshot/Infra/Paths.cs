using Earshot.Contracts;

namespace Earshot.Infra;

// Every folder and file Earshot reads or writes.
//
//   RoamingFolder   %APPDATA%\Earshot                user settings (tray only)
//   LocalFolder     %LOCALAPPDATA%\Earshot           logs and live-test evidence
//   MachineFolder   %ProgramData%\Earshot            gate config, device identity, status files
//   InstallFolder   %ProgramFiles%\Earshot           installed binaries
//
// EARSHOT_DATA_ROOT=<absolute dir> moves the three data folders under <dir> (Roaming, Local and
// ProgramData subfolders) so tests and smoke runs never touch real data. InstallFolder is not a
// data folder and is never moved. EARSHOT_SAFE_MODE turns device actions off (see SafeDecorators).
//
// Both variables are for tests and smoke runs only. A scheduled task running as SYSTEM gets
// SYSTEM's environment, which only an administrator can change, so a user cannot redirect the gate's
// paths with them. An elevated install or uninstall started from a user session may inherit variables
// that user set, so Program.Main refuses gate, install and uninstall while either variable is set
// rather than let their machine-wide writes follow a user-chosen folder.
// https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder
internal sealed class Paths
{
    public const string ProductFolderName = "Earshot";
    public const string DataRootVariable = "EARSHOT_DATA_ROOT";
    public const string SafeModeVariable = "EARSHOT_SAFE_MODE";

    private Paths(string? dataRoot, bool isSafeMode, string roaming, string local, string machine, string install)
    {
        DataRoot = dataRoot;
        IsSafeMode = isSafeMode;
        RoamingFolder = roaming;
        LocalFolder = local;
        MachineFolder = machine;
        InstallFolder = install;
    }

    // The redirect root, or null when the real profile and machine folders are used.
    public string? DataRoot { get; }

    public bool IsRedirected => DataRoot is not null;

    public bool IsSafeMode { get; }

    public string RoamingFolder { get; }

    public string LocalFolder { get; }

    public string MachineFolder { get; }

    public string InstallFolder { get; }

    public string LogFolder => Path.Combine(LocalFolder, "logs");

    public string LiveTestFolder => Path.Combine(LocalFolder, "livetest");

    public string SettingsFile => Path.Combine(RoamingFolder, "settings.json");

    public string LogFile => Path.Combine(LogFolder, "earshot.log");

    public string GateConfigFile => Path.Combine(MachineFolder, "config.json");

    public string DeviceIdentityFile => Path.Combine(MachineFolder, "device.json");

    public string ProtectionRecordFile => Path.Combine(MachineFolder, "protection.json");

    public string InstalledExe => Path.Combine(InstallFolder, "Earshot.exe");

    // The widget's folder, %LOCALAPPDATA%\Earshot\widget, or under EARSHOT_DATA_ROOT when redirected. What an older
    // build kept there (a claim, a proof and set-up records) is no longer read or written by anything; Repair and an
    // update leave it where it is, which WidgetClaimFile lets their tests check.
    public string WidgetFolder => Path.Combine(LocalFolder, "widget");

    public string WidgetClaimFile => Path.Combine(WidgetFolder, "claim.json");

    // The last battery readings of the owner's pair and the learned charge rates (LastReadingStore): values, times and
    // models only, no address or name.
    public string LastReadingFile => Path.Combine(WidgetFolder, "last-reading.json");

    // The paths for this process, read from its environment each time.
    public static Paths Current => FromEnvironment(Environment.GetEnvironmentVariable);

    // Per-request status file written by the gate. The nonce is validated here so a path is
    // never built from an unchecked argument.
    public string StatusFile(string nonce)
    {
        if (!BoundaryValidation.IsNonce(nonce))
        {
            throw new ArgumentException("A status file needs a 32 character lower-case hex nonce.", nameof(nonce));
        }

        return Path.Combine(MachineFolder, "status-" + nonce + ".json");
    }

    public static Paths FromEnvironment(Func<string, string?> getVariable) => FromEnvironment(getVariable, DefaultFolder);

    // getFolder answers for a special folder; only a test passes one that is not the system's. A folder that is not fully
    // qualified is refused, so no path Earshot uses, and no path a service or a SYSTEM task opens, depends on the working
    // directory, which for those is not a folder anyone chose.
    internal static Paths FromEnvironment(Func<string, string?> getVariable, Func<Environment.SpecialFolder, string?> getFolder)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(getFolder);

        bool safeMode = IsSafeModeValue(getVariable(SafeModeVariable));
        string install = Path.Combine(RequireFolder(getFolder, Environment.SpecialFolder.ProgramFiles), ProductFolderName);
        string? root = getVariable(DataRootVariable);

        if (string.IsNullOrWhiteSpace(root))
        {
            return new Paths(
                dataRoot: null,
                isSafeMode: safeMode,
                roaming: Path.Combine(RequireFolder(getFolder, Environment.SpecialFolder.ApplicationData), ProductFolderName),
                local: Path.Combine(RequireFolder(getFolder, Environment.SpecialFolder.LocalApplicationData), ProductFolderName),
                machine: Path.Combine(RequireFolder(getFolder, Environment.SpecialFolder.CommonApplicationData), ProductFolderName),
                install: install);
        }

        root = root.Trim();

        // Fail closed: a relative or drive-relative root would silently land somewhere else,
        // possibly next to real data.
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException(DataRootVariable + " must be an absolute path. Got: " + root);
        }

        root = Path.GetFullPath(root);
        return new Paths(
            dataRoot: root,
            isSafeMode: safeMode,
            roaming: Path.Combine(root, "Roaming", ProductFolderName),
            local: Path.Combine(root, "Local", ProductFolderName),
            machine: Path.Combine(root, "ProgramData", ProductFolderName),
            install: install);
    }

    // Any value other than empty, "0" or "false" turns safe mode on, so a typo errs towards safety.
    internal static bool IsSafeModeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string v = value.Trim();
        return !(v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
    }

    private static string? DefaultFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    // An empty special folder would turn every path relative to the working directory, which for
    // a scheduled task is System32. Refuse instead, and refuse a relative one for the same reason.
    private static string RequireFolder(Func<Environment.SpecialFolder, string?> getFolder, Environment.SpecialFolder folder)
    {
        string? path = getFolder(folder);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Windows did not return the " + folder + " folder.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Windows returned a folder that is not fully qualified for " + folder + ": " + path);
        }

        return path;
    }
}
