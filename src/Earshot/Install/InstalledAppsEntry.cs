using System.Globalization;
using System.Security;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.Win32;

namespace Earshot.Install;

// Where the Installed apps entry lives. Behind an interface so the entry is written and removed against a temporary key
// in the tests, and never against the machine hive.
internal interface IUninstallRegistry
{
    // Creates the entry's key and sets exactly these values, deleting any other value an earlier entry held. A string
    // value is written as a string and an int as a DWORD.
    StepOutcome Write(string subKey, IReadOnlyDictionary<string, object> values);

    // Deletes the entry's key. A key that is not there is already the wanted state.
    StepOutcome Delete(string subKey);
}

// The real root: a base key and a path beneath it. The machine's is HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall
// in the 64-bit view, which is where Settings and Control Panel read installed programs from for a 64-bit program.
// https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key
internal sealed class RegistryUninstallRoot : IUninstallRegistry
{
    public const string MachinePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string WriteStep = "installed-apps-entry";
    private const string DeleteStep = "installed-apps-entry-remove";

    private readonly RegistryKey _baseKey;

    public RegistryUninstallRoot(RegistryKey baseKey, string path)
    {
        ArgumentNullException.ThrowIfNull(baseKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _baseKey = baseKey;
        Path = path;
    }

    public string Path { get; }

    // The machine's Uninstall key, native view. Only the elevated install, update, repair and uninstall runs call this.
    public static RegistryUninstallRoot Machine() =>
        new(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), MachinePath);

    public StepOutcome Write(string subKey, IReadOnlyDictionary<string, object> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        ArgumentNullException.ThrowIfNull(values);
        string full = Path + @"\" + subKey;
        try
        {
            // The entry is replaced as a whole, so a value an earlier version wrote and this one does not is not kept.
            _baseKey.DeleteSubKeyTree(full, throwOnMissingSubKey: false);
            using RegistryKey key = _baseKey.CreateSubKey(full, writable: true);
            foreach ((string name, object value) in values)
            {
                switch (value)
                {
                    case string text:
                        key.SetValue(name, text, RegistryValueKind.String);
                        break;
                    case int number:
                        key.SetValue(name, number, RegistryValueKind.DWord);
                        break;
                    default:
                        return StepOutcomes.NotAttempted(WriteStep, name + " has a value of a kind the entry does not use.");
                }
            }

            return new StepOutcome(WriteStep, true, 0, "S_OK", full);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            // Registry failures carry their HRESULT; the raw code is kept and the step says which key it was.
            // https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.createsubkey
            return StepOutcomes.FromHResult(WriteStep, ex.HResult, full + ": " + ex.Message);
        }
    }

    public StepOutcome Delete(string subKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        string full = Path + @"\" + subKey;
        try
        {
            _baseKey.DeleteSubKeyTree(full, throwOnMissingSubKey: false);
            return new StepOutcome(DeleteStep, true, 0, "S_OK", full);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            return StepOutcomes.FromHResult(DeleteStep, ex.HResult, full + ": " + ex.Message);
        }
    }
}

// The entry that lists Earshot under Settings, Apps, Installed apps and in Control Panel. The value names and what they
// mean are Windows Installer's documented ones; Earshot is not an MSI package and writes the same names itself:
//   DisplayName, DisplayVersion, Publisher, DisplayIcon, InstallLocation, InstallDate, UninstallString, ModifyPath,
//   EstimatedSize (kilobytes, a DWORD) and NoRepair.
// Repair is offered through Modify, so NoRepair is set. ModifyPath runs "repair-ui" and UninstallString "uninstall-ui", the
// unelevated verbs that ask for the one administrator prompt; both commands quote the program's path. That Settings shows
// a Modify button for a program that is not an MSI package rests on the ModifyPath value being documented for it; it was
// not run here.
// https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key
// https://learn.microsoft.com/en-us/windows/win32/msi/arpsize
// https://learn.microsoft.com/en-us/windows/win32/msi/arpnorepair
// https://learn.microsoft.com/en-us/windows/win32/msi/arpproducticon
internal static class InstalledAppsEntry
{
    public const string KeyName = "Earshot";
    public const string Publisher = "5Muawiyah";
    public const string UninstallVerb = "uninstall-ui";
    public const string RepairVerb = "repair-ui";

    public sealed record Measured(string Version, long SizeBytes);

    // The values for an install in installFolder. The folder must be a rooted path with no quote in it: the commands
    // below put it between quotes, and a quote inside it would end them early.
    public static IReadOnlyDictionary<string, object> Build(string installFolder, string version, long sizeBytes, DateOnly installedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        ArgumentNullException.ThrowIfNull(version);
        if (installFolder.Contains('"', StringComparison.Ordinal) || installFolder.Any(char.IsControl) || !Path.IsPathFullyQualified(installFolder))
        {
            throw new ArgumentException("The install folder must be a full path with no quote or control character in it.", nameof(installFolder));
        }

        string folder = Path.TrimEndingDirectorySeparator(installFolder);
        string exe = "\"" + Path.Combine(folder, TaskPlan.ExecutableName) + "\"";
        long kilobytes = Math.Clamp((Math.Max(sizeBytes, 0) + 1023) / 1024, 0, int.MaxValue);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = "Earshot",
            ["DisplayVersion"] = version,
            ["Publisher"] = Publisher,
            ["DisplayIcon"] = exe + ",0",
            ["InstallLocation"] = folder,
            ["EstimatedSize"] = (int)kilobytes,
            ["UninstallString"] = exe + " " + UninstallVerb,
            ["ModifyPath"] = exe + " " + RepairVerb,
            ["NoRepair"] = 1,
            ["InstallDate"] = installedOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        };
    }

    // The installed program's release version (empty when it reports none) and the bytes of every file its manifest lists,
    // plus the manifest. A file the manifest lists that is not there counts for nothing: the size is an estimate.
    public static Measured Measure(string installFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        string exe = Path.Combine(installFolder, TaskPlan.ExecutableName);
        string version = File.Exists(exe) ? SetupValues.ProgramVersion(exe) : "";
        long bytes = 0;
        FileManifest? manifest = FileManifest.Read(installFolder, out _);
        if (manifest is not null)
        {
            string manifestPath = Path.Combine(installFolder, FileManifest.FileName);
            bytes += LengthOrZero(manifestPath);
            foreach (ManifestFile file in manifest.Files)
            {
                bytes += LengthOrZero(Path.Combine(installFolder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        return new Measured(version, bytes);
    }

    public static StepOutcome Write(IUninstallRegistry registry, IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(values);
        return registry.Write(KeyName, values);
    }

    // Measures installFolder and writes the entry for it.
    public static StepOutcome Write(IUninstallRegistry registry, string installFolder, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(time);
        Measured measured = Measure(installFolder);
        IReadOnlyDictionary<string, object> values;
        try
        {
            values = Build(installFolder, measured.Version, measured.SizeBytes, DateOnly.FromDateTime(time.GetLocalNow().DateTime));
        }
        catch (ArgumentException ex)
        {
            return StepOutcomes.NotAttempted("installed-apps-entry", ex.Message);
        }

        return Write(registry, values);
    }

    public static StepOutcome Remove(IUninstallRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Delete(KeyName);
    }

    private static long LengthOrZero(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }
}
