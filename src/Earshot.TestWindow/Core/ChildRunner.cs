using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Earshot.TestWindow.Core;

// Starts one child powershell.exe 5.1 running Invoke-GuiHalf.ps1, and speaks the line protocol
// (test-gui.md sections 5.1 and 5.2) with it. This is the only file, along with FolderOpener.cs,
// that starts a process (T10).
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
        bool allowPlanB)
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

        ProcessStartInfo info = PowerShell51.CreateStartInfo(host, arguments);

        // The preamble defect (test-gui.md section 2, T7.1): a StandardInputEncoding that emits a
        // UTF-8 preamble corrupts the first reply, because the shim reads raw bytes off
        // System.Console.In and the preamble's three bytes land in front of them. ASCII never
        // writes one; the wire is pure ASCII (base64) either way, so nothing this window sends
        // needs more than that.
        info.StandardInputEncoding = Encoding.ASCII;
        info.StandardOutputEncoding = Encoding.UTF8;

        // EARSHOT_SAFE_MODE and EARSHOT_DATA_ROOT are never stripped here (design.md section
        // 8.1): the scripts' own Assert-LiveEnvironment stays the authority on whether a live run
        // may proceed. Only PSModulePath is removed, by PowerShell51.CreateStartInfo.
        _process = new Process { StartInfo = info };
    }

    internal int ProcessId => _process.Id;

    internal void Start()
    {
        _process.Start();
        _readLoop = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        string? line;
        while ((line = _process.StandardOutput.ReadLine()) is not null)
        {
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

    // The one call site for a reply (design.md section 7.4; T7 asserts this from source across
    // the whole project). One reply per seq: a second click for the same seq is ignored, silently,
    // rather than sent twice.
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

    internal void Abort(int seq) => WriteLine(Protocol.FormatAbort(seq));

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
}
