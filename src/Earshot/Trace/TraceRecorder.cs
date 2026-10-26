using System.Diagnostics;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Tracing;

// What the tray calls to record a Bluetooth trace. One recording at a time; one administrator prompt per recording.
internal interface ITraceRecorder : IDisposable
{
    TraceState State { get; }

    // Raised on whichever thread changed the state.
    event EventHandler? StateChanged;

    // True when there is no installed copy of Earshot to raise: the trace is then not offered, and asking for one says to set
    // up Earshot first.
    bool NeedsSetUp { get; }

    // Where a saved trace is copied, for the note shown before recording.
    string DestinationFolder { get; }

    // Asks for the prompt, records until Stop (or ten minutes, or the tray exiting), copies the trace to
    // %LOCALAPPDATA%\Earshot\traces and opens the folder. Never throws for an outcome; the result says what happened.
    Task<TraceResult> RecordAsync(CancellationToken ct);

    void Stop();

    // The notice to show when an earlier helper died with a session open, or null.
    string? StillRunningNotice();

    // The notice to show when run folders of earlier recordings are still under ProgramData, or null.
    string? LeftoverNotice();
}

// InstalledExecutable: %ProgramFiles%\Earshot\Earshot.exe when it is there, otherwise null. Only that copy is ever raised: a copy
// run from Downloads or a build folder sits where a standard user can change it. DestinationFolder: where the person finds the
// trace, %LOCALAPPDATA%\Earshot\traces, which no folder backup uploads (Documents can be).
internal sealed record TraceRecorderOptions(bool SafeMode, Func<string?> InstalledExecutable, string DestinationFolder, int TrayPid, Func<string> NewNonce);

// The tray's side of the user-visible files.
internal interface ITraceUserFiles
{
    // Null when %ProgramData%\Earshot and its trace folder pass the machine folder's check and no folder on the way is a reparse
    // point, so only an administrator can have made what is in them; otherwise why not. Checked before a result is read.
    string? TrustProblem();

    // How many run folders of earlier recordings the trace folder holds.
    int LeftoverRunFolders();

    // Removes copies an Exit cut off, left under their temporary name in destinationFolder; how many were removed.
    int RemovePartialCopies(string destinationFolder);

    string? ReadResult(string nonce);

    // Copies the saved trace to destinationFolder (made if missing). Null when copied; otherwise why not.
    string? Copy(string nonce, string fileName, string destinationFolder, out string copiedTo);

    void OpenFolder(string folder);

    // This session's marker, or null.
    string? ReadMarker();

    bool ProcessAlive(int pid);

    // When this start of the PC began, in Unix seconds (TraceMarker.BootSecondsNow).
    long BootSeconds();
}

internal sealed class TraceRecorder : ITraceRecorder
{
    internal const string DestinationLeaf = "traces";

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

    public bool NeedsSetUp => _options.InstalledExecutable() is null;

    public string DestinationFolder => _options.DestinationFolder;

    public async Task<TraceResult> RecordAsync(CancellationToken ct)
    {
        if (_options.SafeMode)
        {
            return Fail(TraceOutcome.Refused, TraceCopy.SafeMode);
        }

        // Checked before anything is made or asked: with no installed copy there is nothing Earshot may raise.
        if (_options.InstalledExecutable() is not { } executable)
        {
            return Fail(TraceOutcome.Refused, TraceCopy.SetUpFirst);
        }

        string nonce = _options.NewNonce();
        ITrayTraceSignals signals;
        lock (_gate)
        {
            if (_disposed || _state != TraceState.Idle)
            {
                return Fail(TraceOutcome.Refused, TraceCopy.AlreadyRecording);
            }

            // The events exist before the helper is asked for, so the helper has something to open. One that cannot be made (a
            // name already there, or refused) ends the run with nothing asked.
            try
            {
                signals = _signalsFactory.Create(nonce);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                _log.Warn("trace: the events for the helper could not be made: " + ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
                return Fail(TraceOutcome.Refused, TraceCopy.NotStarted);
            }

            _signals = signals;
            _state = TraceState.Recording;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await RecordCoreAsync(executable, nonce, signals, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Earshot is closing. The helper is not asked to stop and save: it watches the tray process, and when that ends it
            // cancels the session with nothing saved, which is what Exit does to a recording.
            return Fail(TraceOutcome.Refused, "The recording was cancelled.");
        }
        finally
        {
            lock (_gate)
            {
                _signals = null;
                _state = TraceState.Idle;
                signals.Dispose();
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<TraceResult> RecordCoreAsync(string executable, string nonce, ITrayTraceSignals signals, CancellationToken ct)
    {
        string arguments = TraceRequest.Verb + " " + _options.TrayPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + nonce;
        _log.Info("trace: asking for the administrator prompt.");

        // ShellExecute with the runas verb returns once the prompt is answered, so it is started away from the caller's thread
        // (the tray's UI thread), which stays free while the prompt is up.
        Task<ElevatedRun> helper = Task.Run(() => _launcher.RunAsync(executable, arguments, ct), CancellationToken.None);
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

        // A result is only believed from a folder nobody but an administrator can have written into.
        if (_files.TrustProblem() is { } untrusted)
        {
            _log.Warn("trace: the trace folder is not trusted, so no result was read from it: " + untrusted);
            signals.SetCopied();
            ElevatedRun refused = await helper.ConfigureAwait(false);
            _log.Info("trace: the helper ended" + ExitText(refused) + ".");
            return Fail(TraceOutcome.HelperFailed, TraceCopy.Untrusted);
        }

        string? text = _files.ReadResult(nonce);
        if (!TraceResultFile.TryParse(text, out TraceResultFile? result) || result is null)
        {
            signals.SetCopied();
            ElevatedRun ended = await helper.ConfigureAwait(false);
            return Fail(TraceOutcome.HelperFailed, "The trace helper finished without a result" + ExitText(ended) + ".");
        }

        TraceResult mapped = await FinishAsync(nonce, signals, result).ConfigureAwait(false);
        ElevatedRun finished = await helper.ConfigureAwait(false);

        // The helper deletes its own copy once the tray has taken the file, or at once when the copy failed, and says so by its
        // exit code when it could not.
        if (result.FileName.Length > 0 && finished.ExitCode == ExitCodes.IoError)
        {
            _log.Warn("trace: the helper could not delete its own copy of the trace.");
            return mapped with { Message = mapped.Message + " " + TraceCopy.AdminCopyKept };
        }

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
                string destination = _options.DestinationFolder;
                string? problem = _files.Copy(nonce, result.FileName, destination, out string copiedTo);
                if (problem is not null)
                {
                    // Nobody could read the helper's copy where it is (only administrators can open that folder), so the helper is
                    // told at once to delete it, rather than wait and leave a file holding addresses and maybe pairing keys.
                    _log.Warn("trace: the saved trace could not be copied: " + problem);
                    signals.SetCopied();
                    return Task.FromResult(Fail(TraceOutcome.SetupFailed, TraceCopy.NotKept(destination, problem)));
                }

                signals.SetCopied();
                _files.OpenFolder(destination);
                _log.Info("trace: saved " + copiedTo + ".");
                return Task.FromResult(new TraceResult(TraceOutcome.Saved, TraceCopy.SavedIn(destination), destination, copiedTo));
            }

            case TraceOutcome.SavedInPlace:
                signals.SetCopied();
                return Task.FromResult(Fail(TraceOutcome.SavedInPlace,
                    @"The trace was recorded but could not be handed to you. It is in the Earshot\trace folder under ProgramData, which only administrators can open."));

            case TraceOutcome.WprMissing:
                signals.SetCopied();
                return Task.FromResult(Fail(TraceOutcome.WprMissing, TraceCopy.WprMissing));

            case TraceOutcome.StoppedBeforeStart:
                signals.SetCopied();
                return Task.FromResult(Fail(TraceOutcome.StoppedBeforeStart, TraceCopy.StoppedBeforeStart));

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

    // Under the lock, because the run's own end disposes the signals under it after it has let go of them: a press of Stop as
    // the run ends either reaches signals that are still there or finds none.
    public void Stop()
    {
        lock (_gate)
        {
            _signals?.RequestStop();
        }
    }

    // Only a marker in a trace folder that passes its check, from this start of the PC, whose helper is gone. The helper's id
    // can be reused by another process within the same start, which only keeps the notice back.
    public string? StillRunningNotice()
    {
        string? text = _files.ReadMarker();
        if (text is null)
        {
            return null;
        }

        if (_files.TrustProblem() is { } untrusted)
        {
            _log.Info("trace: a marker is there, and it is not used, because the trace folder did not pass its check: " + untrusted);
            return null;
        }

        if (!TraceMarker.TryParse(text, out TraceMarker? marker) || marker is null)
        {
            return null;
        }

        if (!TraceMarker.SameBoot(marker.BootSeconds, _files.BootSeconds()))
        {
            _log.Info("trace: the marker is not from this start of the PC (or the clock moved by more than 30 s since), so it is not taken to mean a recording is running.");
            return null;
        }

        return _files.ProcessAlive(marker.HelperPid) ? null : TraceCopy.StillRunning;
    }

    public string? LeftoverNotice()
    {
        // A copy cut off by Exit holds addresses and is no use, so it goes at the next start; it never had the trace's own name.
        int partial = _files.RemovePartialCopies(_options.DestinationFolder);
        if (partial > 0)
        {
            _log.Info("trace: " + partial.ToString(System.Globalization.CultureInfo.InvariantCulture) + " copy(ies) cut off by an earlier exit were removed.");
        }

        int count = _files.LeftoverRunFolders();

        // The helper refuses a trace folder that fails the machine folder's check, so none of Earshot's recordings can be in one;
        // folders in it are not called administrators' own, or said to hold keys.
        if (count > 0 && _files.TrustProblem() is { } untrusted)
        {
            _log.Warn("trace: the trace folder did not pass its check, so its " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " folder(s) are not reported as earlier recordings: " + untrusted);
            return null;
        }
        return count > 0 ? TraceCopy.Leftover(count) : null;
    }

    public void Dispose()
    {
        // A recording does not outlive the tray that asked for it: the helper watches the tray process and, when it ends, cancels
        // the session with nothing saved. It is not asked to stop here, which would save a file nobody collects.
        lock (_gate)
        {
            _disposed = true;
        }
    }
}

// The real files: the helper's result and trace under %ProgramData%\Earshot\trace\<nonce>, readable by this user because the
// helper granted it, and the folder the person finds the copy in.
internal sealed class TraceUserFiles : ITraceUserFiles
{
    private readonly string _traceFolder;
    private readonly IFolderSecurity _folders;
    private readonly ILog _log;

    public TraceUserFiles(string traceFolder, IFolderSecurity folders, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFolder);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(log);
        _traceFolder = traceFolder;
        _folders = folders;
        _log = log;
    }

    private static string Describe(Exception ex) =>
        ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message;

    public string? TrustProblem()
    {
        string machine = Path.GetDirectoryName(Path.GetFullPath(_traceFolder)) ?? _traceFolder;
        if (TraceMachine.FirstReparsePoint(_traceFolder) is { } linked)
        {
            return linked + " is a reparse point.";
        }

        foreach (string folder in new[] { machine, _traceFolder })
        {
            StepOutcome read = _folders.ReadSddl(folder, out string? sddl);
            if (!read.Ok)
            {
                return read.Step + " " + read.CodeName + ": " + read.Detail;
            }

            IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(sddl);
            if (problems.Count > 0)
            {
                return folder + ": " + string.Join(" ", problems);
            }
        }

        return null;
    }

    public int LeftoverRunFolders()
    {
        try
        {
            return Directory.Exists(_traceFolder)
                ? Directory.EnumerateDirectories(_traceFolder).Count(d => BoundaryValidation.IsNonce(Path.GetFileName(d)))
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warn("trace: the trace folder could not be listed: " + Describe(ex));
            return 0;
        }
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
            _log.Warn("trace: the result could not be read: " + Describe(ex));
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

        // Copied under a temporary name and renamed once whole, so an Exit part way leaves no file that looks like a trace; the
        // next start removes the temporary one (RemovePartialCopies).
        string partial = "";
        try
        {
            Directory.CreateDirectory(destinationFolder);
            copiedTo = Path.Combine(destinationFolder, fileName);
            if (File.Exists(copiedTo))
            {
                return copiedTo + " is already there.";
            }

            partial = copiedTo + PartialSuffix;
            File.Copy(Path.Combine(RunFolder(nonce), fileName), partial, overwrite: true);
            File.Move(partial, copiedTo, overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Describe(ex) + DeletePartial(partial);
        }
    }

    internal const string PartialSuffix = ".partial";

    // "" when there is nothing to delete or it went; otherwise what stopped it, after the copy's own failure.
    private static string DeletePartial(string partial)
    {
        if (partial.Length == 0 || !File.Exists(partial))
        {
            return "";
        }

        try
        {
            File.Delete(partial);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return " The part copied could not be removed: " + Describe(ex);
        }
    }

    public int RemovePartialCopies(string destinationFolder)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(destinationFolder))
            {
                return 0;
            }

            foreach (string partial in Directory.EnumerateFiles(destinationFolder, "*.etl" + PartialSuffix))
            {
                try
                {
                    File.Delete(partial);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn("trace: a copy cut off by an earlier exit could not be removed: " + Describe(ex));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("trace: the traces folder could not be read for copies cut off by an earlier exit: " + Describe(ex));
        }

        return removed;
    }

    internal static string ExplorerPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // The trace is already saved when this runs, so a folder window that does not open is logged and nothing more.
    public void OpenFolder(string folder)
    {
        // By its full path, so no explorer.exe beside Earshot or in the current folder is started instead.
        var info = new ProcessStartInfo(ExplorerPath) { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        try
        {
            using Process? started = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn("trace: the folder window could not be opened: " + Describe(ex));
        }
    }

    // A marker is a few short lines; a bigger file is not one, and is not read (a folder that has not been checked yet may hold
    // anything).
    internal const int MaxMarkerBytes = 1024;

    public string? ReadMarker()
    {
        try
        {
            string path = Path.Combine(_traceFolder, TraceHelper.MarkerFileName);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxMarkerBytes)
            {
                _log.Warn("trace: the marker is " + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes, which no marker is, so it is not read.");
                return null;
            }

            return File.ReadAllText(path);
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
            _log.Warn("trace: the marker could not be read: " + Describe(ex));
            return null;
        }
    }

    public long BootSeconds() => TraceMarker.BootSecondsNow();

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
        catch (System.ComponentModel.Win32Exception ex)
        {
            // A process this user may not query: another account's, a protected one that reused the id, or, under Administrator
            // protection, the helper itself, which runs as another account. It counts as gone, so at worst the notice says a trace
            // may still be running when it is ending: the safe side, against missing a recording session left behind.
            _log.Warn("trace: process " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " could not be queried (" + Describe(ex) + "), so it counts as gone.");
            return false;
        }
    }
}
