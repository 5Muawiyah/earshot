using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Earshot.Tests.Update;

namespace Earshot.Tests.Installer;

// A local stand-in for the two GitHub hosts the installer script talks to: the release API and the release downloads.
// It rides on the existing loopback FakeReleaseServer. Every file is served as application/octet-stream, as GitHub serves
// release assets (the API answers JSON). Nothing leaves the machine. Every request is recorded with the names of the
// headers it carried, so a test can say the script sent only what the shells send by default.
internal sealed class InstallerFeed : IDisposable
{
    public const string ApiPath = "/repos/5Muawiyah/earshot/releases/latest";

    private const string DownloadPrefix = "/5Muawiyah/earshot/releases/download/";

    private readonly object _gate = new();
    private readonly List<FeedRequest> _requests = new();

    public InstallerFeed(string? tag = null, ReleaseZipBuilder? zip = null)
    {
        Server = new FakeReleaseServer();
        Latest = AddRelease(tag ?? BuildVersion.NewerTag, zip);
        PublishLatest(Latest);
    }

    public FakeReleaseServer Server { get; }

    public FeedRelease Latest { get; private set; }

    // The base the script is given with -Feed: the loopback address with its trailing slash.
    public string FeedAddress => Server.Root.ToString();

    public IReadOnlyList<FeedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public int ApiRequests => Requests.Count(r => r.Path == ApiPath);

    public int Downloads(string suffix) => Requests.Count(r => r.Path.StartsWith(DownloadPrefix, StringComparison.Ordinal) && r.Path.EndsWith(suffix, StringComparison.Ordinal));

    public int ZipDownloads => Downloads(".zip");

    public int ChecksumDownloads => Downloads(".zip.sha256");

    // The release's zip and checksum, served under its own tag. The checksum is what a correct release publishes unless a
    // test says otherwise.
    public FeedRelease AddRelease(string tag, ReleaseZipBuilder? zip = null, Func<FeedRelease, byte[]>? checksum = null)
    {
        string version = tag.TrimStart('v');
        string zipName = "Earshot-" + version + "-win-x64.zip";
        byte[] bytes = (zip ?? new ReleaseZipBuilder()).Build();
        var release = new FeedRelease(tag, version, zipName, bytes, ReleaseZipBuilder.Sha256Hex(bytes));
        ServeFile(DownloadPrefix + tag + "/" + zipName, bytes);
        ServeFile(DownloadPrefix + tag + "/" + zipName + ".sha256", checksum?.Invoke(release) ?? Encoding.UTF8.GetBytes(release.ZipHash.ToLowerInvariant() + "  " + zipName + "\n"));
        return release;
    }

    public void ReplaceChecksum(FeedRelease release, byte[] body) =>
        ServeFile(DownloadPrefix + release.Tag + "/" + release.ZipName + ".sha256", body);

    public void ReplaceZip(FeedRelease release, byte[] body) =>
        ServeFile(DownloadPrefix + release.Tag + "/" + release.ZipName, body);

    public void Remove(FeedRelease release, string suffix = "")
    {
        string path = DownloadPrefix + release.Tag + "/" + release.ZipName + suffix;
        Server.Map(path, context =>
        {
            Record(context);
            Respond(context, HttpStatusCode.NotFound, "text/plain", Encoding.UTF8.GetBytes("status 404"));
        });
    }

    // What the latest-release address answers. A release with no assets, or with other assets, makes each failure.
    public void PublishLatest(FeedRelease release, bool zipAsset = true, bool checksumAsset = true, string? tagName = null, bool draft = false, bool prerelease = false)
    {
        var assets = new List<string>();
        if (zipAsset)
        {
            assets.Add("{\"name\":\"" + release.ZipName + "\"}");
        }

        if (checksumAsset)
        {
            assets.Add("{\"name\":\"" + release.ZipName + ".sha256\"}");
        }

        assets.Add("{\"name\":\"earshot.ps1\"}");
        string json = "{\"tag_name\":\"" + (tagName ?? release.Tag) + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (prerelease ? "true" : "false") +
                      ",\"assets\":[" + string.Join(",", assets) + "]}";
        Latest = release;
        Server.Map(ApiPath, context =>
        {
            Record(context);
            Respond(context, HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(json));
        });
    }

    public void ApiAnswers(HttpStatusCode status) =>
        Server.Map(ApiPath, context =>
        {
            Record(context);
            Respond(context, status, "text/plain", Encoding.UTF8.GetBytes("status " + (int)status));
        });

    // earshot.ps1 itself, so a test can run it the way a person does: irm <address> | iex.
    public void ServeScript(byte[] script) => ServeFile("/earshot.ps1", script);

    private void ServeFile(string path, byte[] body) =>
        Server.Map(path, context =>
        {
            Record(context);
            Respond(context, HttpStatusCode.OK, "application/octet-stream", body);
        });

    private void Record(HttpListenerContext context)
    {
        string[] headers = context.Request.Headers.AllKeys.Where(k => k is not null).Select(k => k!).ToArray();
        lock (_gate)
        {
            _requests.Add(new FeedRequest(context.Request.Url!.AbsolutePath, context.Request.UserAgent, headers));
        }
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string contentType, byte[] body)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    public void Dispose() => Server.Dispose();
}

internal sealed record FeedRelease(string Tag, string Version, string ZipName, byte[] Zip, string ZipHash);

internal sealed record FeedRequest(string Path, string? UserAgent, IReadOnlyList<string> HeaderNames);

// CommandLineToArgvW, the function Windows programs use to read a command line back into arguments: what a program started
// with a line receives. Used to show that the line the script builds is read back as the arguments the verbs expect.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
internal static class ArgvSplitter
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);

    // The arguments after the program name. The line is given without the program, so a dummy name leads it.
    public static string[] Split(string argumentLine)
    {
        nint list = CommandLineToArgvW("program.exe " + argumentLine, out int count);
        if (list == 0)
        {
            throw new InvalidOperationException("CommandLineToArgvW failed with " + Marshal.GetLastWin32Error() + ".");
        }

        try
        {
            string[] arguments = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                arguments[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!;
            }

            return arguments;
        }
        finally
        {
            LocalFree(list);
        }
    }

    // Splits a line that starts with a quoted or plain program path: [program, arguments...].
    public static string[] SplitWithProgram(string line)
    {
        nint list = CommandLineToArgvW(line, out int count);
        if (list == 0)
        {
            throw new InvalidOperationException("CommandLineToArgvW failed with " + Marshal.GetLastWin32Error() + ".");
        }

        try
        {
            string[] arguments = new string[count];
            for (int i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!;
            }

            return arguments;
        }
        finally
        {
            LocalFree(list);
        }
    }
}
