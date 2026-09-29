using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// A release zip the way the release script lays it out: everything under Earshot\, with Earshot.files.json listing
// each file's path and SHA-256.
internal sealed class ReleaseZipBuilder
{
    public string Prefix { get; set; } = "Earshot/";

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
    {
        ["Earshot.exe"] = RandomNumberGenerator.GetBytes(4096),
        ["Earshot.dll"] = RandomNumberGenerator.GetBytes(60_000),
        ["runtimes/native.txt"] = Encoding.ASCII.GetBytes("native"),
    };

    // Present in the zip but not named by the file list.
    public HashSet<string> Unlisted { get; } = new(StringComparer.Ordinal);

    // The hash the file list records instead of the file's real one.
    public Dictionary<string, string> WrongHash { get; } = new(StringComparer.Ordinal);

    // Entries written with exactly this name and content, listed or not (a zip-slip name, an absolute path).
    public List<(string Name, byte[] Data)> RawEntries { get; } = new();

    public bool IncludeManifest { get; set; } = true;

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public byte[] Build()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in Files)
            {
                Add(archive, Prefix + name, data);
            }

            foreach ((string name, byte[] data) in RawEntries)
            {
                Add(archive, name, data);
            }

            if (IncludeManifest)
            {
                IEnumerable<string> entries = Files
                    .Where(f => !Unlisted.Contains(f.Key))
                    .Select(f => "    { \"Path\": \"" + f.Key + "\", \"Sha256\": \"" + (WrongHash.TryGetValue(f.Key, out string? wrong) ? wrong : Sha256Hex(f.Value)) + "\" }");
                string manifest = "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n";
                Add(archive, Prefix + "Earshot.files.json", Encoding.UTF8.GetBytes(manifest));
            }
        }

        return memory.ToArray();
    }

    private static void Add(ZipArchive archive, string name, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}

// A fake feed with one release on it, and the routes it points at.
internal sealed class FeedFixture : IDisposable
{
    public const string FeedPath = "/repos/5Muawiyah/earshot/releases/latest";

    public FeedFixture(string tag = "v1.2.0", ReleaseZipBuilder? zip = null)
    {
        Tag = tag;
        Version = tag.TrimStart('v');
        ZipName = "Earshot-" + Version + "-win-x64.zip";
        ChecksumName = ZipName + ".sha256";
        Zip = (zip ?? new ReleaseZipBuilder()).Build();
        ZipHash = ReleaseZipBuilder.Sha256Hex(Zip);
        Server = new FakeReleaseServer();
        Server.MapBytes("/dl/" + ZipName, Zip);
        Server.MapText("/dl/" + ChecksumName, ZipHash.ToLowerInvariant() + "  " + ZipName + "\n");
        Publish();
    }

    public FakeReleaseServer Server { get; }

    public string Tag { get; }

    public string Version { get; }

    public string ZipName { get; }

    public string ChecksumName { get; }

    public byte[] Zip { get; }

    public string ZipHash { get; }

    public string ZipAddress => Server.Address("/dl/" + ZipName).ToString();

    public string ChecksumAddress => Server.Address("/dl/" + ChecksumName).ToString();

    public Uri Feed => Server.Address(FeedPath);

    // Serves the release JSON for the current settings of the fixture. Assets can be left out or given other
    // addresses to make each failure.
    public void Publish(bool zipAsset = true, bool checksumAsset = true, string? zipUrl = null, string? checksumUrl = null, string? tagOverride = null, string? extra = null)
    {
        var assets = new List<string>();
        if (zipAsset)
        {
            assets.Add("{\"name\":\"" + ZipName + "\",\"size\":" + Zip.Length + ",\"browser_download_url\":\"" + (zipUrl ?? ZipAddress) + "\"}");
        }

        if (checksumAsset)
        {
            assets.Add("{\"name\":\"" + ChecksumName + "\",\"size\":80,\"browser_download_url\":\"" + (checksumUrl ?? ChecksumAddress) + "\"}");
        }

        assets.Add("{\"name\":\"Source.zip\",\"browser_download_url\":\"" + Server.Address("/dl/Source.zip") + "\"}");
        Server.MapJson(FeedPath, "{\"tag_name\":\"" + (tagOverride ?? Tag) + "\",\"draft\":false,\"prerelease\":false" +
                                 (extra is null ? "" : "," + extra) + ",\"assets\":[" + string.Join(",", assets) + "]}");
    }

    public UpdateService Service(ILog log, string stagingRoot, ReleaseVersion running, UpdateTimeouts? timeouts = null) =>
        new(log, running, stagingRoot, Feed, allowLoopbackHttp: true, timeouts ?? new UpdateTimeouts(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)));

    public void Dispose() => Server.Dispose();
}

internal static class UpdateAsserts
{
    public static readonly ReleaseVersion Running = new(1, 1, 0);

    // The staging root holds no download folder, and no file was left in it.
    public static void StagingIsEmpty(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        string[] left = Directory.GetFileSystemEntries(stagingRoot, "*", SearchOption.AllDirectories);
        Assert.IsEmpty(left, "The staging folder was left holding: " + string.Join(", ", left));
    }
}

// An update source that answers what a test tells it and counts every call.
internal sealed class FakeUpdateSource : IUpdateSource
{
    public int CheckCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public Func<CancellationToken, Task<UpdateCheckResult>> OnCheck { get; set; } =
        _ => Task.FromResult(UpdateCheckResult.UpToDate(new ReleaseVersion(1, 1, 0)));

    public Func<ReleaseInfo, IProgress<UpdateProgress>?, CancellationToken, Task<UpdateDownloadResult>> OnDownload { get; set; } =
        (_, _, _) => throw new AssertFailedException("A download was not expected.");

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        CheckCalls++;
        return OnCheck(ct);
    }

    public Task<UpdateDownloadResult> DownloadAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        DownloadCalls++;
        return OnDownload(release, progress, ct);
    }

    public static ReleaseInfo Release(string version = "1.2.0") =>
        new(ReleaseVersion.TryParse(version, out ReleaseVersion parsed) ? parsed : throw new AssertFailedException("bad version"),
            "v" + version, "Earshot-" + version + "-win-x64.zip", new Uri("https://example.test/a.zip"),
            "Earshot-" + version + "-win-x64.zip.sha256", new Uri("https://example.test/a.sha256"), 1000);
}

// Records what the controller asks it to start, and answers what a test says. Never starts anything.
internal sealed class FakeUpdateLauncher : IUpdateLauncher
{
    public List<(string Executable, string[] Arguments, string WorkingDirectory)> Launches { get; } = new();

    public Func<string, LaunchResult> OnLaunch { get; set; } =
        executable => new LaunchResult(LaunchOutcome.Started, 0, executable, null);

    // Runs inside Launch, before it answers: where a test looks at the state of the staged files at hand-over.
    public Action<string>? DuringLaunch { get; set; }

    public LaunchResult Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Launches.Add((executable, arguments.ToArray(), workingDirectory));
        DuringLaunch?.Invoke(executable);
        return OnLaunch(executable);
    }
}
