using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Earshot.Tests.Update;

// A release feed and its downloads, served by HttpListener on a loopback port, inside the test process. Nothing
// leaves the machine. Each route is a path mapped to a handler; every request is recorded.
internal sealed class FakeReleaseServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Action<HttpListenerContext>> _routes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly Task _loop;

    public FakeReleaseServer()
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = FreePort();
            string prefix = "http://127.0.0.1:" + port + "/";
            var listener = _listener;
            listener.Prefixes.Clear();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                Root = new Uri(prefix);
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                // The port was taken between choosing it and listening on it; choose another.
            }
        }

        _loop = Task.Run(Loop);
    }

    public Uri Root { get; }

    public Uri Address(string path) => new(Root, path.TrimStart('/'));

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public int Count(string path) => Requests.Count(r => r.Path == path);

    public void Map(string path, Action<HttpListenerContext> handler)
    {
        lock (_gate)
        {
            _routes[path] = handler;
        }
    }

    public void MapJson(string path, string json) =>
        Map(path, context => Send(context, HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(json)));

    public void MapBytes(string path, byte[] bytes) =>
        Map(path, context => Send(context, HttpStatusCode.OK, "application/octet-stream", bytes));

    public void MapText(string path, string text) => MapBytes(path, Encoding.UTF8.GetBytes(text));

    public void MapStatus(string path, HttpStatusCode status) =>
        Map(path, context => Send(context, status, "text/plain", Encoding.UTF8.GetBytes("status " + (int)status)));

    public void MapRedirect(string path, string location, HttpStatusCode status = HttpStatusCode.Found) =>
        Map(path, context =>
        {
            context.Response.StatusCode = (int)status;
            context.Response.RedirectLocation = location;
            context.Response.OutputStream.Close();
        });

    // Says the body is full.Length bytes long, sends only the first sent of them, and drops the connection.
    public void MapTruncated(string path, byte[] full, int sent) =>
        Map(path, context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = full.Length;
            context.Response.OutputStream.Write(full, 0, sent);
            context.Response.OutputStream.Flush();
            context.Response.Abort();
        });

    // Sends the first sent bytes, then holds the connection open until the server is disposed: a download that stalls.
    public void MapStalled(string path, byte[] full, int sent) =>
        Map(path, context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = full.Length;
            context.Response.OutputStream.Write(full, 0, sent);
            context.Response.OutputStream.Flush();
            _release.Wait(TimeSpan.FromSeconds(30));
            context.Response.Abort();
        });

    private readonly ManualResetEventSlim _release = new(false);

    private static void Send(HttpListenerContext context, HttpStatusCode status, string contentType, byte[] body)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(path, context.Request.Headers["User-Agent"], context.Request.Headers["Accept"], context.Request.HttpMethod));
        }

        Action<HttpListenerContext>? handler;
        lock (_gate)
        {
            _routes.TryGetValue(path, out handler);
        }

        try
        {
            if (handler is null)
            {
                Send(context, HttpStatusCode.NotFound, "text/plain", Encoding.UTF8.GetBytes("no route"));
            }
            else
            {
                handler(context);
            }
        }
        catch (HttpListenerException)
        {
            // The client went away while the handler wrote; a cut-short download does this on purpose.
        }
        catch (InvalidOperationException)
        {
            // The response was already aborted by the handler.
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        _release.Set();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends on the closed listener.
        }

        _release.Dispose();
    }
}

internal sealed record RecordedRequest(string Path, string? UserAgent, string? Accept, string Method);
