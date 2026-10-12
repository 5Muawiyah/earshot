using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Earshot.Setup;

namespace Earshot.Tests.Setup;

// A release zip as the release script writes it: everything under an Earshot folder, with Earshot.files.json listing every
// other file by relative path and SHA-256.
internal static class ReleaseZip
{
    public static byte[] Build(string version, IReadOnlyDictionary<string, byte[]>? extra = null, Func<string, string>? hashOf = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Earshot.exe"] = Encoding.ASCII.GetBytes("exe " + version),
            ["Earshot.dll"] = Encoding.ASCII.GetBytes("dll " + version),
            ["runtimes/native.txt"] = Encoding.ASCII.GetBytes("native " + version),
        };
        if (extra is not null)
        {
            foreach ((string name, byte[] bytes) in extra)
            {
                files[name] = bytes;
            }
        }

        var list = new StringBuilder("{\"SchemaVersion\":1,\"Files\":[");
        list.Append(string.Join(",", files.Select(f =>
            "{\"Path\":\"" + f.Key + "\",\"Sha256\":\"" + (hashOf?.Invoke(f.Key) ?? Convert.ToHexString(SHA256.HashData(f.Value))) + "\"}")));
        list.Append("]}");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in files)
            {
                Add(zip, "Earshot/" + name, bytes);
            }

            Add(zip, "Earshot/Earshot.files.json", Encoding.UTF8.GetBytes(list.ToString()));
        }

        return stream.ToArray();
    }

    public static byte[] FromEntries(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                Add(zip, name, bytes);
            }
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using Stream s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed class FakePayload(byte[] zip, string version, string? recordedHash = null, bool embedded = true) : IPayload
{
    public bool Embedded => embedded;

    public string Sha256 { get; } = recordedHash ?? ReleaseZip.Sha256(zip);

    public string Version => version;

    public int Opens { get; private set; }

    public Stream Open()
    {
        Opens++;
        return new MemoryStream(zip, writable: false);
    }
}

// Creates plain folders and reads back the SDDL a test chooses.
internal sealed class FakeAcl : IFolderAcl
{
    public List<string> Created { get; } = [];

    public List<string> ReadPaths { get; } = [];

    public Func<string, string?> ReadAs { get; set; } = path => path.EndsWith(StagingFolder.FolderName, StringComparison.OrdinalIgnoreCase) || path.Contains(StagingFolder.FolderName, StringComparison.OrdinalIgnoreCase)
        ? StagingAcl.AdminOnlySddl
        : StagingAcl.MachineFolderSddl;

    public bool FailCreate { get; set; }

    public bool Exists(string path) => Directory.Exists(path);

    public AclStep CreateWithSddl(string path, string sddl, bool failIfExists)
    {
        if (FailCreate)
        {
            return new AclStep(false, unchecked((int)0x80070005), "denied");
        }

        if (failIfExists && Directory.Exists(path))
        {
            return new AclStep(false, NtfsFolderAcl.NotAttempted, "exists");
        }

        Directory.CreateDirectory(path);
        Created.Add(path + "|" + sddl);
        return new AclStep(true, 0, path);
    }

    public AclStep ReadSddl(string path, out string? sddl)
    {
        ReadPaths.Add(path);
        sddl = ReadAs(path);
        return sddl is null ? new AclStep(false, unchecked((int)0x80070003), "missing") : new AclStep(true, 0, path);
    }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<(string Exe, string[] Args)> Runs { get; } = [];

    public List<string> UserSessionStarts { get; } = [];

    public Func<string, string[], LaunchResult>? Handler { get; set; }

    // What "probe setup-values" writes.
    public string ValuesJson { get; set; } = "";

    public LaunchResult RunAndWait(string exePath, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        string[] args = [.. arguments];
        Runs.Add((exePath, args));
        if (args.Length >= 4 && args[0] == "probe" && args[1] == "setup-values")
        {
            File.WriteAllText(args[3], ValuesJson);
            return new LaunchResult(true, 0, "");
        }

        return Handler?.Invoke(exePath, args) ?? new LaunchResult(true, 0, "");
    }

    public LaunchResult StartInUserSession(string exePath)
    {
        UserSessionStarts.Add(exePath);
        return new LaunchResult(true, 0, "");
    }
}

internal sealed class ScriptedUi : ISetupUi
{
    public List<string> Log { get; } = [];

    public Func<IReadOnlyList<SetupAction>, SetupAction> Choose { get; set; } = offered => offered[0];

    public int? Pick { get; set; } = 0;

    public List<(MessageKind Kind, string Instruction, string Content)> Messages { get; } = [];

    public List<IReadOnlyList<SetupAction>> Offers { get; } = [];

    public List<string> Names { get; } = [];

    public SetupAction ChooseAction(string instruction, string content, string footer, IReadOnlyList<SetupAction> offered)
    {
        Log.Add("choose");
        Offers.Add(offered);
        Footer = footer;
        return Choose(offered);
    }

    public string Footer { get; private set; } = "";

    public int? ChooseAirPods(IReadOnlyList<string> names)
    {
        Log.Add("airpods");
        Names.AddRange(names);
        return Pick;
    }

    public void Show(MessageKind kind, string instruction, string content)
    {
        Log.Add("show");
        Messages.Add((kind, instruction, content));
    }

    public T Work<T>(string instruction, string content, Func<T> work)
    {
        Log.Add("work");
        return work();
    }
}

internal sealed class FakeHost(string machineFolder, string installedExe, bool elevated = true) : ISetupHost
{
    public bool IsElevated => elevated;

    public string MachineFolder => machineFolder;

    public string InstalledExe => installedExe;
}
