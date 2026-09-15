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
// A scheduled task running as SYSTEM gets SYSTEM's environment, which only an administrator can
// change, so a user cannot redirect the gate's paths with these variables.
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

    public static Paths FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        bool safeMode = IsSafeModeValue(getVariable(SafeModeVariable));
        string install = Path.Combine(RequireFolder(Environment.SpecialFolder.ProgramFiles), ProductFolderName);
        string? root = getVariable(DataRootVariable);

        if (string.IsNullOrWhiteSpace(root))
        {
            return new Paths(
                dataRoot: null,
                isSafeMode: safeMode,
                roaming: Path.Combine(RequireFolder(Environment.SpecialFolder.ApplicationData), ProductFolderName),
                local: Path.Combine(RequireFolder(Environment.SpecialFolder.LocalApplicationData), ProductFolderName),
                machine: Path.Combine(RequireFolder(Environment.SpecialFolder.CommonApplicationData), ProductFolderName),
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

    // An empty special folder would turn every path relative to the working directory, which for
    // a scheduled task is System32. Refuse instead.
    private static string RequireFolder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Windows did not return the " + folder + " folder.");
        }

        return path;
    }
}
