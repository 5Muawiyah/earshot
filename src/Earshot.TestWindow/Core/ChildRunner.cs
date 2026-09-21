using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Earshot.TestWindow.Core;

// Starts one child powershell.exe 5.1 running Invoke-GuiHalf.ps1, and speaks the line protocol
// with it. This is the only file that starts a process.
internal sealed class ChildRunner : IDisposable
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly HashSet<int> _repliedSeq = new();
    private bool _stdinClosed;
    private Task? _readLoop;

    internal event Action<string>? TranscriptLine;
    internal event Action<ChildMessage>? MessageReceived;

    internal ChildRunner(
        string host,
        string driverScript,
        string script,
        string exePath,
        string runRoot,
        bool resume,
        int variant,
        bool offerUninstall,
        bool allowPlanB,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        IReadOnlyList<(string Name, string Value)>? extraArguments = null)
    {
        var arguments = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", driverScript,
            "-Script", script, "-ExePath", exePath, "-RunRoot", runRoot,
        };

        if (resume)
        {
            arguments.Add("-Resume");
        }

        if (variant != 0)
        {
            arguments.Add("-Variant");
            arguments.Add(variant.ToString(CultureInfo.InvariantCulture));
        }

        if (offerUninstall)
        {
            arguments.Add("-OfferUninstall");
        }

        if (allowPlanB)
        {
            arguments.Add("-AllowPlanB");
        }

        // The sandbox driver's own extra parameters (-SandboxRoot, -TestId, -Case): never sent to
        // the production driver, which declares none of them.
        if (extraArguments is not null)
        {
            foreach ((string name, string value) in extraArguments)
            {
                arguments.Add("-" + name);
                arguments.Add(value);
            }
        }

        ProcessStartInfo info = PowerShell51.CreateStartInfo(host, arguments);

        // A StandardInputEncoding that emits a UTF-8 preamble corrupts the first reply, because
        // the shim reads raw bytes off System.Console.In and the preamble's three bytes land in
        // front of them. ASCII never
        // writes one; the wire is pure ASCII (base64) either way, so nothing this window sends
        // needs more than that.
        info.StandardInputEncoding = Encoding.ASCII;
        info.StandardOutputEncoding = Encoding.UTF8;

        // EARSHOT_SAFE_MODE and EARSHOT_DATA_ROOT are never stripped here: the scripts' own
        // Assert-LiveEnvironment stays the authority on whether a live run may proceed. Only
        // PSModulePath is removed, by PowerShell51.CreateStartInfo.
        //
        // --sandbox (development and tests only) redirects LOCALAPPDATA, APPDATA, ProgramData and
        // ProgramFiles for the child into a folder of its own, so Run-GuiHalfAgainstFakes.ps1 and
        // the real, unstubbed New-LiveTestRun/Get-EarshotDataPaths never touch this machine's real
        // %LOCALAPPDATA%\Earshot.
        if (environmentOverrides is not null)
        {
            foreach ((string name, string value) in environmentOverrides)
            {
                info.Environment[name] = value;
            }
        }

        _process = new Process { StartInfo = info };
    }

    internal int ProcessId => _process.Id;

    internal void Start()
    {
        _process.Start();
        _readLoop = Task.Run(ReadLoop);
    }

    // Priority-zero fix (hosted build crash): this runs on a ThreadPool thread (Task.Run) with
    // nothing above it to catch an exception, so anything this loop lets escape is unhandled on a
    // background thread by construction. Killing a real child (the window's own forced hard stop, or a test
    // harness's own cleanup) can break its stdout pipe before ReadLine reaches a clean end of
    // stream instead of after, and an IOException or ObjectDisposedException from that is not a
    // sign anything is wrong: it is what a killed process's pipe does. Treated exactly like a
    // clean EOF (the loop simply ends); MessageReceived/TranscriptLine's own subscribers
    // (MainForm's SafeBeginInvoke) are what is responsible for a child that ends without an exit
    // message being noticed at all, not this loop.
    private void ReadLoop()
    {
        while (true)
        {
            string? line;
            try
            {
                line = _process.StandardOutput.ReadLine();
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (Protocol.IsProtocolLine(line))
            {
                MessageReceived?.Invoke(Protocol.ParseMessage(line));
            }
            else
            {
                TranscriptLine?.Invoke(line);
            }
        }
    }

    // The one call site for a reply, asserted from source across the whole project. One reply
    // per seq: a second click for the same seq is ignored, silently, rather than sent twice.
    internal void ReplyFromOwnerClick(int seq, string reply)
    {
        lock (_gate)
        {
            if (!_repliedSeq.Add(seq))
            {
                return;
            }
        }

        WriteLine(Protocol.FormatReply(seq, reply));
    }

    // Reserves the seq the same way a reply does, before the abort line is even written: a click
    // racing this abort for the same seq (the owner presses a button the instant before Stop is
    // processed, or the reverse) must never still send a reply for a prompt this abort has
    // already told the script to give up on.
    // Reserves the seq the same way a reply does, before the abort line is even written: a click
    // racing this abort for the same seq (the owner presses a button the instant before Stop is
    // processed, or the reverse) must never still send a reply for a prompt this abort has
    // already told the script to give up on.
    internal void Abort(int seq)
    {
        lock (_gate)
        {
            _repliedSeq.Add(seq);
        }

        WriteLine(Protocol.FormatAbort(seq));
    }

    internal bool HasReplyOrAbortBeenSentForTests(int seq)
    {
        lock (_gate)
        {
            return _repliedSeq.Contains(seq);
        }
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_stdinClosed)
            {
                return;
            }

            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
    }

    // The window closing, or a hard stop: stdin reaches end of input, which is what makes the
    // shim throw "The test window closed before this was answered" inside the script.
    internal void CloseInput()
    {
        lock (_gate)
        {
            if (_stdinClosed)
            {
                return;
            }

            _stdinClosed = true;
            _process.StandardInput.Close();
        }
    }

    internal bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    internal int ExitCode => _process.ExitCode;

    internal void Kill() => _process.Kill(entireProcessTree: true);

    internal Task WaitForReadLoopAsync() => _readLoop ?? Task.CompletedTask;

    public void Dispose()
    {
        _process.Dispose();
    }

    // The one other process this window ever starts: a short, read-only, no-window run of
    // Get-PowerCycleEvidence.ps1, which touches no device and needs no elevation. Kept as a
    // static helper on this same class rather than a new file, so "process starts only in
    // ChildRunner.cs" stays true by construction.
    internal static string RunPowerCycleProbe(string host, string scriptPath, DateTimeOffset sinceUtc, TimeSpan timeout)
    {
        var arguments = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-SinceUtc", sinceUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) };
        ProcessStartInfo info = PowerShell51.CreateStartInfo(host, arguments);
        info.StandardOutputEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return "{\"error\":\"Get-PowerCycleEvidence.ps1 did not finish within " + timeout + ".\"}";
        }

        process.WaitForExit();
        return output.ToString().Trim();
    }
}
