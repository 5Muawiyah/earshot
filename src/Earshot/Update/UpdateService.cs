using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Update;

// How long the network side waits. Waiting budgets, not measured figures.
internal sealed record UpdateTimeouts(TimeSpan Request, TimeSpan Stall)
{
    // Request: the whole of a check, and how long the answer to a download request may take to start. Stall: how
    // long a download may go without a single byte arriving.
    public static UpdateTimeouts Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
}

// Reads the latest release of github.com/5Muawiyah/earshot and, when the owner asks, downloads and unpacks it.
//
// The feed address is a compile-time constant. The only other way in is the internal constructor below, which
// tests use to point at a server on the loopback interface: nothing a user can write (the settings file, an
// environment variable, the registry) reaches either the feed address or a download address. This class reads
// none of them.
//
// Every address is HTTPS. The one exception is a test's fake server: an instance built by the internal constructor
// with allowLoopbackHttp accepts plain HTTP for loopback hosts only. Redirects are followed by hand so each hop is
// checked the same way; a redirect to anything that is not HTTPS is refused, and no credential is ever sent.
//
// Nothing is downloaded by a check. A download is checked against the SHA-256 the release publishes beside it
// before anything is unpacked, and a failure at any step deletes what it had put in the staging folder.
// https://docs.github.com/en/rest/releases/releases#get-the-latest-release
// https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
internal sealed partial class UpdateService : IUpdateSource
{
    // The one feed address in production. GitHub's REST API answers a release request for a repository with the
    // newest published release that is not a draft or a pre-release.
    internal const string LatestReleaseAddress = "https://api.github.com/repos/5Muawiyah/earshot/releases/latest";

    internal const int MaxRedirects = 5;
    internal const int MaxReleaseBytes = 1024 * 1024;
    internal const int MaxChecksumBytes = 4096;
    internal const long MaxZipBytes = 512L * 1024 * 1024;
    private const int BufferBytes = 81920;

    private readonly ILog _log;
    private readonly ReleaseVersion _running;
    private readonly string _stagingRoot;
    private readonly Uri _feed;
    private readonly bool _allowLoopbackHttp;
    private readonly UpdateTimeouts _timeouts;

    // The production service. stagingRoot is where downloads are unpacked, under the data folder.
    public UpdateService(ILog log, ReleaseVersion running, string stagingRoot)
        : this(log, running, stagingRoot, new Uri(LatestReleaseAddress), allowLoopbackHttp: false, UpdateTimeouts.Default)
    {
    }

    // For tests: a feed of their own, and optionally plain HTTP to a loopback host.
    internal UpdateService(ILog log, ReleaseVersion running, string stagingRoot, Uri feed, bool allowLoopbackHttp, UpdateTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(timeouts);
        _log = log;
        _running = running;
        _stagingRoot = stagingRoot;
        _feed = feed;
        _allowLoopbackHttp = allowLoopbackHttp;
        _timeouts = timeouts;
    }

    internal string StagingRoot => _stagingRoot;

    internal Uri Feed => _feed;

    // The address of the release published for one version: the same feed, the tag's own release in place of the latest.
    // https://docs.github.com/en/rest/releases/releases#get-a-release-by-tag-name
    internal Uri TagAddress(ReleaseVersion version) => new(_feed, "tags/v" + version);

    // The release published for exactly this version, found the way a check finds the latest (the same feed, the same
    // HTTPS and size rules, the same asset names), and then downloaded and checked against its checksum file by
    // DownloadAsync. Downloads nothing.
    public async Task<UpdateCheckResult> FindReleaseAsync(ReleaseVersion version, CancellationToken ct)
    {
        try
        {
            return await CheckCoreAsync(TagAddress(version), version, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            UpdateFailure failure = Describe(ex, "check", ct);
            _log.Warn("Repair: the release of " + version + " could not be found (" + failure.Kind + "): " + failure.Detail, failure.Kind == UpdateFailureKind.Cancelled ? null : ex);
            return UpdateCheckResult.Failed(failure);
        }
    }

    // Reads the latest release and says whether it is newer than the running program. Downloads nothing.
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            return await CheckCoreAsync(_feed, exact: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            UpdateFailure failure = Describe(ex, "check", ct);
            _log.Warn("Update check failed (" + failure.Kind + "): " + failure.Detail, failure.Kind == UpdateFailureKind.Cancelled ? null : ex);
            return UpdateCheckResult.Failed(failure);
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(Uri address, ReleaseVersion? exact, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeouts.Request);
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await GetAsync(http, address, "application/vnd.github+json", deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw StatusFailure(response, "the release feed");
        }

        byte[] body = await ReadBoundedAsync(response.Content, MaxReleaseBytes, "the release feed", deadline.Token).ConfigureAwait(false);
        ReleaseInfo? release = ParseRelease(body, exact, out ReleaseVersion latest);
        if (exact is not null && release is not null)
        {
            _log.Info("Repair: the release of " + latest + " was found.");
            return UpdateCheckResult.Available(release);
        }

        if (release is null)
        {
            // Older or the same: no asset is needed, so a release that never carried Windows files still reads as
            // "up to date" for a machine that is not behind it.
            _log.Info("Update check: the latest release is " + latest + ", running " + _running + ". Up to date.");
            return UpdateCheckResult.UpToDate(latest);
        }

        _log.Info("Update check: the latest release is " + latest + ", running " + _running + ". An update is available.");
        return UpdateCheckResult.Available(release);
    }

    // The release the feed named when it is newer than the running program, or null when it is not (latest says
    // which version it named). A feed that is not what this expects, or a newer release with no usable files, throws.
    //
    // exact: the one version wanted, as a repair asks for it: the tag must be that version, and it is returned whether or
    // not it is newer than the running program.
    private ReleaseInfo? ParseRelease(byte[] body, ReleaseVersion? exact, out ReleaseVersion latest)
    {
        using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UpdateException(UpdateFailureKind.BadResponse, "The release feed's answer was not a JSON object.");
        }

        string tag = RequireString(root, "tag_name", "the release feed");
        foreach (string flag in new[] { "draft", "prerelease" })
        {
            if (root.TryGetProperty(flag, out JsonElement value) && value.ValueKind == JsonValueKind.True)
            {
                throw new UpdateException(UpdateFailureKind.BadResponse, "The release feed named a release marked " + flag + ".");
            }
        }

        if (!ReleaseVersion.TryParse(tag, out latest))
        {
            throw new UpdateException(UpdateFailureKind.BadResponse, "The release tag '" + Bound(tag) + "' is not a version.");
        }

        if (exact is { } wanted)
        {
            if (latest != wanted)
            {
                throw new UpdateException(UpdateFailureKind.BadResponse, "The release feed named " + tag + " when " + wanted + " was asked for.");
            }
        }
        else if (latest <= _running)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new UpdateException(UpdateFailureKind.NoZipAsset, "The release has no assets list.");
        }

        string zipName = "Earshot-" + latest + "-win-x64.zip";
        string checksumName = zipName + ".sha256";
        Uri? zip = null;
        Uri? checksum = null;
        long? zipSize = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? name = nameElement.GetString();
            bool isZip = string.Equals(name, zipName, StringComparison.Ordinal);
            bool isChecksum = string.Equals(name, checksumName, StringComparison.Ordinal);
            if (!isZip && !isChecksum)
            {
                continue;
            }

            Uri address = ReadAssetAddress(asset, name!);
            if (isZip && zip is null)
            {
                zip = address;
                zipSize = asset.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long bytes) && bytes > 0 ? bytes : null;
            }
            else if (isChecksum && checksum is null)
            {
                checksum = address;
            }
        }

        if (zip is null)
        {
            throw new UpdateException(UpdateFailureKind.NoZipAsset, "The release " + tag + " lists no asset named " + zipName + ".");
        }

        if (checksum is null)
        {
            throw new UpdateException(UpdateFailureKind.NoChecksumAsset, "The release " + tag + " lists no asset named " + checksumName + ".");
        }

        return new ReleaseInfo(latest, tag, zipName, zip, checksumName, checksum, zipSize);
    }

    private Uri ReadAssetAddress(JsonElement asset, string name)
    {
        string text = RequireString(asset, "browser_download_url", "asset " + name);
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? address))
        {
            throw new UpdateException(UpdateFailureKind.BadResponse, "The address of asset " + name + " is not an address.");
        }

        RequireAllowed(address);
        return address;
    }

    private static string RequireString(JsonElement element, string property, string what)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw new UpdateException(UpdateFailureKind.BadResponse, what + " has no text member '" + property + "'.");
        }

        return value.GetString()!;
    }

    // ----- HTTP -----

    private HttpClient CreateClient()
    {
        // Redirects are followed by GetAsync, so each hop is checked. No cookies, no credentials.
        // https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = _timeouts.Request,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // One GET, following redirects by hand. The caller disposes the response.
    private async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri address, string accept, CancellationToken ct)
    {
        Uri current = address;
        for (int hop = 0; ; hop++)
        {
            RequireAllowed(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Earshot", _running.ToString()));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/5Muawiyah/earshot)"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            Uri? location = response.Headers.Location;
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            if (location is null)
            {
                throw new UpdateException(UpdateFailureKind.BadResponse, "A " + (int)status + " redirect carried no Location.");
            }

            if (hop >= MaxRedirects)
            {
                throw new UpdateException(UpdateFailureKind.BadResponse, "More than " + MaxRedirects + " redirects.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    // HTTPS only, with no user name in it. Plain HTTP passes for a loopback host, and only for an instance a test
    // built to allow it.
    internal void RequireAllowed(Uri address)
    {
        bool secure = address.Scheme == Uri.UriSchemeHttps;
        bool loopbackTest = _allowLoopbackHttp && address.Scheme == Uri.UriSchemeHttp && address.IsLoopback;
        if (!secure && !loopbackTest)
        {
            throw new UpdateException(UpdateFailureKind.NotHttps, "Refused an address that is not HTTPS: " + address.Scheme + "://" + address.Host + ".");
        }

        if (address.UserInfo.Length > 0)
        {
            throw new UpdateException(UpdateFailureKind.NotHttps, "Refused an address that carries a user name: " + address.Scheme + "://" + address.Host + ".");
        }
    }

    private static UpdateException StatusFailure(HttpResponseMessage response, string what)
    {
        int code = (int)response.StatusCode;
        return new UpdateException(UpdateFailureKind.HttpStatus, what + " answered " + code.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ".", code);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, string what, CancellationToken ct)
    {
        long? declared = content.Headers.ContentLength;
        if (declared > limit)
        {
            throw new UpdateException(UpdateFailureKind.TooLarge, what + " declared " + declared + " bytes, over the " + limit + " byte limit.");
        }

        using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new UpdateException(UpdateFailureKind.TooLarge, what + " was over the " + limit + " byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (declared is long expected && buffer.Length != expected)
        {
            throw new UpdateException(UpdateFailureKind.Truncated, what + " ended after " + buffer.Length + " of " + expected + " bytes.");
        }

        return buffer.ToArray();
    }

    // ----- Failures -----

    internal static string Bound(string text) => text.Length <= 60 ? text : text[..60] + "...";

    // The plain sentence for a person, per kind.
    internal static string ReasonFor(UpdateFailureKind kind, int? status = null) => kind switch
    {
        UpdateFailureKind.Network => "Couldn't reach GitHub. Check your connection.",
        UpdateFailureKind.TimedOut => "GitHub took too long to answer.",
        UpdateFailureKind.HttpStatus when status is 403 or 429 => "GitHub is limiting requests. Try again later.",
        UpdateFailureKind.HttpStatus when status is 404 => "GitHub has no such release or file (404).",
        UpdateFailureKind.HttpStatus => "GitHub answered with an error (" + (status?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ").",
        UpdateFailureKind.BadResponse => "GitHub's answer was not what Earshot expected.",
        UpdateFailureKind.NotHttps => "The update link was not a secure link, so Earshot did not use it.",
        UpdateFailureKind.NoZipAsset => "The latest release has no Windows download.",
        UpdateFailureKind.NoChecksumAsset => "The latest release has no checksum file, so Earshot will not install it.",
        UpdateFailureKind.ChecksumMalformed => "The checksum file could not be read, so Earshot did not install the update.",
        UpdateFailureKind.ChecksumMismatch => "The download did not match its checksum, so Earshot removed it.",
        UpdateFailureKind.Truncated => "The download stopped part way. Nothing was changed.",
        UpdateFailureKind.TooLarge => "The download was larger than expected, so Earshot stopped. Nothing was changed.",
        UpdateFailureKind.BadArchive => "The update file was damaged or not what Earshot expected. Nothing was changed.",
        UpdateFailureKind.StagingFailed => "Earshot could not save the update on this PC. Nothing was changed.",
        UpdateFailureKind.Cancelled => "Cancelled.",
        _ => "Something went wrong.",
    };

    // Turns what a step threw into a failure: the kind and plain sentence for the person, and the raw cause for the
    // log (the HTTP status, or the exception's type and code).
    internal static UpdateFailure Describe(Exception ex, string doing, CancellationToken userToken)
    {
        ArgumentNullException.ThrowIfNull(ex);
        switch (ex)
        {
            case UpdateException update:
                return new UpdateFailure(update.Kind, ReasonFor(update.Kind, update.Status), update.Message);
            case OperationCanceledException when userToken.IsCancellationRequested:
                return new UpdateFailure(UpdateFailureKind.Cancelled, ReasonFor(UpdateFailureKind.Cancelled), "The " + doing + " was cancelled.");
            case OperationCanceledException:
                return new UpdateFailure(UpdateFailureKind.TimedOut, ReasonFor(UpdateFailureKind.TimedOut), "The " + doing + " timed out (" + ex.GetType().Name + ").");
            case HttpRequestException http:
                return new UpdateFailure(UpdateFailureKind.Network, ReasonFor(UpdateFailureKind.Network),
                    nameof(HttpRequestException) + " (" + http.HttpRequestError + (http.StatusCode is { } code ? ", status " + (int)code : "") + ")" +
                    (http.InnerException is null ? "" : ", inner " + http.InnerException.GetType().Name) + ": " + http.Message);
            case JsonException:
                return new UpdateFailure(UpdateFailureKind.BadResponse, ReasonFor(UpdateFailureKind.BadResponse), "The answer was not valid JSON (" + ex.GetType().Name + "): " + ex.Message);
            case InvalidDataException:
                return new UpdateFailure(UpdateFailureKind.BadArchive, ReasonFor(UpdateFailureKind.BadArchive), "The zip could not be read (" + ex.GetType().Name + "): " + ex.Message);
            case IOException io when IsNetworkIo(io):
                // The connection ended while a body was being read: a check has nothing partial to keep, so it is
                // a connection problem; a download stopped part way.
                UpdateFailureKind ended = doing == "check" ? UpdateFailureKind.Network : UpdateFailureKind.Truncated;
                return new UpdateFailure(ended, ReasonFor(ended),
                    "The connection ended early (" + ex.GetType().Name + ", HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + ex.Message);
            case IOException or UnauthorizedAccessException:
                return new UpdateFailure(UpdateFailureKind.StagingFailed, ReasonFor(UpdateFailureKind.StagingFailed),
                    "The " + doing + " could not use the disk (" + ex.GetType().Name + ", HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + ex.Message);
            default:
                throw new InvalidOperationException("Describe was given an exception it does not handle.", ex);
        }
    }

    // An IOException from reading a response body is an HttpIOException; one from the disk is not. The response
    // types are the only source of the first.
    private static bool IsNetworkIo(IOException io) =>
        io is HttpIOException || io.InnerException is System.Net.Sockets.SocketException;

    // Thrown inside this class to end a step with a kind of failure; caught at the two entry points.
    internal sealed class UpdateException : Exception
    {
        public UpdateException(UpdateFailureKind kind, string message, int? status = null)
            : base(message)
        {
            Kind = kind;
            Status = status;
        }

        public UpdateFailureKind Kind { get; }

        public int? Status { get; }
    }

    // The checksum file: "<64 hex>  <file name>" (the sha256sum format), one line, upper or lower case hex. The name
    // must be the zip's own, so a checksum published for another file never vouches for this one.
    internal static bool TryParseChecksum(byte[] bytes, string zipName, out string hex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        hex = "";
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        string line = text.TrimEnd('\r', '\n');
        if (line.Contains('\n', StringComparison.Ordinal) || line.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        const int hexLength = 64;
        if (line.Length != hexLength + 2 + zipName.Length || !line.AsSpan(0, hexLength).ToString().All(Uri.IsHexDigit))
        {
            return false;
        }

        string separator = line.Substring(hexLength, 2);
        if (separator != "  " && separator != " *")
        {
            return false;
        }

        if (!string.Equals(line[(hexLength + 2)..], zipName, StringComparison.Ordinal))
        {
            return false;
        }

        hex = line[..hexLength];
        return true;
    }
}
