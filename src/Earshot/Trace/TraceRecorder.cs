using System.Diagnostics;
using Earshot.Boot;
using Earshot.Contracts;

namespace Earshot.Tracing;

// What the tray calls to record a Bluetooth trace. One recording at a time; one administrator prompt per recording.
internal interface ITraceRecorder : IDisposable
{
    TraceState State { get; }

    // Raised on whichever thread changed the state.
    event EventHandler? StateChanged;

    // Asks for the prompt, records until Stop (or ten minutes, or the tray exiting), copies the trace to
    // Documents\Earshot\Bluetooth traces and opens the folder. Never throws for an outcome; the result says what happened.
    Task<TraceResult> RecordAsync(CancellationToken ct);

    void Stop();

    // The notice to show when an earlier helper died with a session open, or null.
    string? StillRunningNotice();
}

internal sealed record TraceRecorderOptions(bool SafeMode, string Executable, string DocumentsFolder, int TrayPid, Func<string> NewNonce);

// The tray's side of the user-visible files.
internal interface ITraceUserFiles
{
    string? ReadResult(string nonce);

    // Copies the saved trace to destinationFolder (made if missing). Null when copied; otherwise why not.
    string? Copy(string nonce, string fileName, string destinationFolder, out string copiedTo);

    void OpenFolder(string folder);

    string? ReadMarker();

    bool ProcessAlive(int pid);
}

internal sealed class TraceRecorder : ITraceRecorder
{
    internal const string DestinationLeaf = "Bluetooth traces";

    private readonly TraceRecorderOptions _options;
    private readonly IElevatedLauncher _launcher;
    private readonly ITrayTraceSignalsFactory _signalsFactory;
    private readonly ITraceUserFiles _files;
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private ITrayTraceSignals? _signals;
    private TraceState _state = TraceState.Idle;
    private bool _disposed;

    public TraceRecorder(TraceRecorderOptions options, IElevatedLauncher launcher, ITrayTraceSignalsFactory signalsFactory, ITraceUserFiles files, ILog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(signalsFactory);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);
        _options = options;
        _launcher = launcher;
        _signalsFactory = signalsFactory;
        _files = files;
        _log = log;
    }

    public event EventHandler? StateChanged;

    public TraceState State
    {
        get { lock (_gate) { return _state; } }
    }

    public async Task<TraceResult> RecordAsync(CancellationToken ct)
    {
        if (_options.SafeMode)
        {
            return Fail(TraceOutcome.Refused, TraceCopy.SafeMode);
        }

        string nonce = _options.NewNonce();
        ITrayTraceSignals signals;
        lock (_gate)
        {
            if (_disposed || _state != TraceState.Idle)
            {
                return Fail(TraceOutcome.Refused, TraceCopy.AlreadyRecording);
            }

            // The events exist before the helper is asked for, so the helper has something to open.
            signals = _signalsFactory.Create(nonce);
            _signals = signals;
            _state = TraceState.Recording;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await RecordCoreAsync(nonce, signals, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up. The helper is asked to stop and save, so nothing is left recording.
            signals.RequestStop();
            return Fail(TraceOutcome.Refused, "The recording was cancelled.");
        }
        finally
        {
            lock (_gate)
            {
                _signals = null;
                _state = TraceState.Idle;
            }

            signals.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<TraceResult> RecordCoreAsync(string nonce, ITrayTraceSignals signals, CancellationToken ct)
    {
        string arguments = TraceRequest.Verb + " " + _options.TrayPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + nonce;
        _log.Info("trace: asking for the administrator prompt.");
        Task<ElevatedRun> helper = _launcher.RunAsync(_options.Executable, arguments, ct);
        using var waitCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<bool> done = Task.Run(() => signals.WaitDone(waitCancel.Token), CancellationToken.None);

        Task first = await Task.WhenAny(helper, done).ConfigureAwait(false);
        bool saidDone = first == done && await done.ConfigureAwait(false);
        if (!saidDone)
        {
            // The helper ended (or the wait was cancelled) before saying done. It sets done before it exits, so look once more.
            ElevatedRun ended = await helper.ConfigureAwait(false);
            await waitCancel.CancelAsync().ConfigureAwait(false);
            _ = await done.ConfigureAwait(false);
            saidDone = IsDoneNow(signals);
            if (!saidDone)
            {
                return Ended(ended);
            }
        }

        string? text = _files.ReadResult(nonce);
        if (!TraceResultFile.TryParse(text, out TraceResultFile? result) || result is null)
        {
            signals.SetCopied();
            ElevatedRun ended = await helper.ConfigureAwait(false);
            return Fail(TraceOutcome.HelperFailed, "The trace helper finished without a result" + ExitText(ended) + ".");
        }

        TraceResult mapped = await FinishAsync(nonce, signals, result).ConfigureAwait(false);
        await helper.ConfigureAwait(false);
        return mapped;
    }

    private static bool IsDoneNow(ITrayTraceSignals signals)
    {
        using var immediate = new CancellationTokenSource();
        immediate.Cancel();
        return signals.WaitDone(immediate.Token);
    }

    private Task<TraceResult> FinishAsync(string nonce, ITrayTraceSignals signals, TraceResultFile result)
    {
        switch (result.Outcome)
        {
            case TraceOutcome.Saved:
            {
                string destination = Path.Combine(_options.DocumentsFolder, "Earshot", DestinationLeaf);
                string? problem = _files.Copy(nonce, result.FileName, destination, out string copiedTo);
                if (problem is not null)
                {
                    // The helper keeps its copy: it is only deleted once the tray says it is finished with it.
                    _log.Warn("trace: the saved trace could not be copied: " + problem);
                    return Task.FromResult(Fail(TraceOutcome.SetupFailed,
                        "The trace was recorded but could not be copied to Documents (" + problem + "). It is still in the Earshot folder under ProgramData, which only administrators can open."));
                }

                signals.SetCopied();
                _files.OpenFolder(destination);
                _log.Info("trace: saved " + copiedTo + ".");
                return Task.FromResult(new TraceResult(TraceOutcome.Saved, TraceCopy.Saved, destination, copiedTo));
            }

            case TraceOutcome.SavedInPlace:
                signals.SetCopied();
                return Task.FromResult(Fail(TraceOutcome.SavedInPlace,
                    @"The trace was recorded but could not be handed to you. It is in the Earshot\trace folder under ProgramData, which only administrators can open."));

            case TraceOutcome.WprMissing:
                signals.SetCopied();
                return Task.FromResult(Fail(TraceOutcome.WprMissing, TraceCopy.WprMissing));

            default:
                signals.SetCopied();
                return Task.FromResult(Fail(result.Outcome,
                    "The Bluetooth trace could not be recorded (" + result.Outcome + (result.WprCode != 0 ? ", " + TraceCopy.Encoded(result.WprCode) : "") + ")."));
        }
    }

    private TraceResult Ended(ElevatedRun ended)
    {
        if (ended.ExitCode is null)
        {
            // ShellExecute's runas reports a declined prompt as ERROR_CANCELLED (1223).
            // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.verb
            if (ended.Step.Code == 1223)
            {
                _log.Info("trace: the administrator prompt was declined.");
                return Fail(TraceOutcome.Declined, TraceCopy.Declined);
            }

            return Fail(TraceOutcome.HelperFailed, "The trace helper could not be started (" + ended.Step.CodeName + ").");
        }

        return Fail(TraceOutcome.HelperFailed, "The trace helper ended" + ExitText(ended) + " without saving a trace.");
    }

    private static string ExitText(ElevatedRun run) =>
        run.ExitCode is int code ? " with exit code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    private TraceResult Fail(TraceOutcome outcome, string message)
    {
        _log.Info("trace: " + outcome + ": " + message);
        return new TraceResult(outcome, message, null, null);
    }

    public void Stop()
    {
        ITrayTraceSignals? signals;
        lock (_gate)
        {
            signals = _signals;
        }

        signals?.RequestStop();
    }

    public string? StillRunningNotice()
    {
        if (!TraceMarker.TryParse(_files.ReadMarker(), out TraceMarker? marker) || marker is null)
        {
            return null;
        }

        return _files.ProcessAlive(marker.HelperPid) ? null : TraceCopy.StillRunning;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        // A recording must not outlive the tray that asked for it.
        Stop();
    }
}

// The real files: the helper's result and trace under %ProgramData%\Earshot\trace\<nonce>, readable by this user because the
// helper granted it, and the folder the person finds the copy in.
internal sealed class TraceUserFiles : ITraceUserFiles
{
    private readonly string _traceFolder;

    public TraceUserFiles(string traceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFolder);
        _traceFolder = traceFolder;
    }

    private string RunFolder(string nonce)
    {
        if (!BoundaryValidation.IsNonce(nonce))
        {
            throw new ArgumentException("A run folder needs a 32 character lower-case hex nonce.", nameof(nonce));
        }

        return Path.Combine(_traceFolder, nonce);
    }

    public string? ReadResult(string nonce)
    {
        try
        {
            return File.ReadAllText(Path.Combine(RunFolder(nonce), TraceHelper.ResultFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Earshot trace: the result could not be read: " + ex.Message);
            return null;
        }
    }

    public string? Copy(string nonce, string fileName, string destinationFolder, out string copiedTo)
    {
        copiedTo = "";
        if (fileName.Length == 0 || !TraceResultFile.IsBareName(fileName) || !fileName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return "The trace file's name is not one Earshot made.";
        }

        try
        {
            Directory.CreateDirectory(destinationFolder);
            copiedTo = Path.Combine(destinationFolder, fileName);
            File.Copy(Path.Combine(RunFolder(nonce), fileName), copiedTo, overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    public void OpenFolder(string folder)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        using Process? started = Process.Start(info);
    }

    public string? ReadMarker()
    {
        try
        {
            return File.ReadAllText(Path.Combine(_traceFolder, TraceHelper.MarkerFileName));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Earshot trace: the marker could not be read: " + ex.Message);
            return null;
        }
    }

    public bool ProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
