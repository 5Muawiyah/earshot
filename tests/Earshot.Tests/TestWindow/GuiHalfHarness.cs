using System.Collections.Concurrent;
using Earshot.TestWindow.Core;

namespace Earshot.Tests.TestWindow;

// Starts a real Invoke-GuiHalf.ps1 child against a script of the test's own choosing, and turns
// ChildRunner's events into a single sequential feed a test can consume with a timeout. Used by
// every S3 protocol test (T6, T7, T7.1) so each one only states the script and the sequence of
// prompts and replies it expects, not the process plumbing.
internal sealed class GuiHalfHarness : IDisposable
{
    private readonly TempFolder _root = new();
    private readonly BlockingCollection<object> _events = new();
    private readonly List<string> _transcript = new();
    private readonly object _transcriptGate = new();

    internal string RunRoot { get; }

    internal ChildRunner Runner { get; }

    // Every transcript line the child has printed so far, kept independently of _events: a test
    // that only waits for a particular message (NextMessageOfKind) must not lose the plain
    // output lines it skips past on the way there.
    internal string Transcript
    {
        get
        {
            lock (_transcriptGate)
            {
                return string.Join("\n", _transcript);
            }
        }
    }

    internal GuiHalfHarness(string scriptContent, bool resume = false, int variant = 0, string driverScript = "Invoke-GuiHalf.ps1")
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            throw new InvalidOperationException("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string scriptPath = Path.Combine(_root.Path, "probe.ps1");
        File.WriteAllText(scriptPath, scriptContent);

        RunRoot = Path.Combine(_root.Path, "run");
        Directory.CreateDirectory(RunRoot);

        string driver = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "gui", driverScript);
        if (!File.Exists(driver))
        {
            throw new InvalidOperationException("The driver was not found at " + driver + ".");
        }

        Runner = new ChildRunner(host, driver, scriptPath, @"C:\nowhere\Earshot.exe", RunRoot, resume, variant, offerUninstall: false, allowPlanB: false);
        Runner.MessageReceived += message => _events.Add(message);
        Runner.TranscriptLine += line =>
        {
            lock (_transcriptGate)
            {
                _transcript.Add(line);
            }

            _events.Add(line);
        };
    }

    internal void Start() => Runner.Start();

    // The next event (a ChildMessage or a plain transcript string), or throws if none arrives.
    internal object Next(TimeSpan timeout)
    {
        if (!_events.TryTake(out object? item, timeout))
        {
            throw new TimeoutException("No event arrived within " + timeout + ".");
        }

        return item;
    }

    internal ChildMessage NextMessage(TimeSpan timeout)
    {
        object item = Next(timeout);
        return item as ChildMessage ?? throw new InvalidOperationException("Expected a message but got a transcript line: " + item);
    }

    internal ChildMessage NextMessageOfKind(ChildMessageKind kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            object item = Next(deadline - DateTime.UtcNow);
            if (item is ChildMessage message && message.Kind == kind)
            {
                return message;
            }
        }

        throw new TimeoutException("No message of kind " + kind + " arrived within " + timeout + ".");
    }

    public void Dispose()
    {
        Runner.Dispose();
        _root.Dispose();
    }
}
