using System.Globalization;
using System.Security.Principal;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Tracing;

// Everything the elevated helper touches on the machine, so tests run the helper without one.
internal interface ITraceMachine
{
    string WprPath { get; }

    // %ProgramData%\Earshot\trace
    string TraceFolder { get; }

    bool FileExists(string path);

    // Makes the trace folder (administrators and SYSTEM write, others read) and, inside it, a new folder for this run that
    // only administrators and SYSTEM can open. The machine folder and the trace folder are read back and must pass the
    // machine folder's check, and no folder on the way may be a reparse point. Null when all hold; otherwise why not.
    string? PrepareRunFolder(string nonce, out string runFolder);

    // Null when written; otherwise why not.
    string? WriteFile(string path, byte[] bytes);

    // Null when read; a failure is logged with its code.
    byte[]? ReadFile(string path);

    // Null when deleted; otherwise why not, which is logged too.
    string? DeleteFile(string path);

    // Null when deleted; otherwise why not, which is logged too. Only a folder inside the trace folder is ever deleted.
    string? DeleteRunFolder(string runFolder);

    // Gives the user who runs the tray (read at the start from the tray's own process handle) read access to one file. Null
    // when done; otherwise why not.
    string? GrantRead(string path, SecurityIdentifier trayUser);
}

// The tray's three events and the tray process, as the helper sees them.
internal interface ITraceSignals
{
    // Opens the events the tray made. Null when all are open; otherwise why not.
    string? Open();

    bool TrayRunning();

    // True when the tray process named on the command line is in this helper's own Windows session: the trace is made readable
    // for that process's user, so it must be the one at this screen.
    bool TrayInThisSession();

    // The account the tray runs as, read from the process handle Open took, so a process ID used again by another process
    // after the tray exits cannot be the one the trace is given to. Null with the reason when it cannot be read.
    SecurityIdentifier? TrayUser(out string? problem);

    // Blocks until the tray asks to stop, the tray process exits, or max passes.
    TraceStopReason WaitForStop(TimeSpan max);

    // True when the tray has already asked to stop (Stop pressed while the administrator prompt was up). Does not wait.
    bool StopAsked();

    void SignalDone();

    // True when the tray said it has finished with the run folder.
    bool WaitCopied(TimeSpan max);
}

// The elevated half of "Record a Bluetooth trace": one administrator prompt, the whole recording inside it.
//
//   trace <tray pid> <nonce>
//
// It starts the Bluetooth stack's verbose Windows Performance Recorder profile, waits for the tray to say stop (or for ten
// minutes), saves the trace into a folder only administrators can open, lets the tray's user read the file, and deletes its own
// copy once the tray has copied it, or after a wait when the tray never says so. A tray that exits while recording ends the
// session with nothing saved. It never writes into a folder the user can
// change, and it changes no device, service or pairing.
internal static class TraceHelper
{
    public static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CopyWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CancelLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopLimit = TimeSpan.FromMinutes(3);

    internal const string ProfileFileName = "BluetoothStack.wprp";
    internal const string ResultFileName = "result.txt";
    // One per Windows session, as the wpr instance name is, so two people tracing at once keep their own.
    internal static string MarkerFileName { get; } = MarkerFileNameFor(System.Diagnostics.Process.GetCurrentProcess().SessionId);

    internal static string MarkerFileNameFor(int sessionId) => "running-S" + sessionId.ToString(CultureInfo.InvariantCulture) + ".txt";

    public static TraceOutcome Run(TraceRequest request, ITraceMachine machine, ITraceSignals signals, IProcessRunner runner, ILog log, Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(utcNow);

        string? openProblem = signals.Open();
        if (openProblem is not null)
        {
            log.Warn("trace: the tray's signals could not be opened, so nothing was started: " + openProblem);
            return TraceOutcome.Refused;
        }

        if (!signals.TrayRunning())
        {
            log.Warn("trace: the tray (process " + request.TrayPid.ToString(CultureInfo.InvariantCulture) + ") is not running, so nothing was started.");
            return TraceOutcome.Refused;
        }

        if (!signals.TrayInThisSession())
        {
            log.Warn("trace: the tray (process " + request.TrayPid.ToString(CultureInfo.InvariantCulture) + ") is in another Windows session, so nothing was started.");
            return TraceOutcome.Refused;
        }

        SecurityIdentifier? trayUser = signals.TrayUser(out string? userProblem);
        if (trayUser is null)
        {
            log.Warn("trace: the tray's account could not be read, so nothing was started: " + userProblem);
            return TraceOutcome.Refused;
        }

        string? folderProblem = machine.PrepareRunFolder(request.Nonce, out string runFolder);
        if (folderProblem is not null)
        {
            log.Warn("trace: the private folder could not be made, so nothing was started: " + folderProblem);
            signals.SignalDone();
            return TraceOutcome.SetupFailed;
        }

        TraceOutcome outcome = Record(request, machine, signals, runner, log, utcNow, runFolder, trayUser, out string fileName, out int wprCode, out bool leaveFolder);

        string resultPath = Path.Combine(runFolder, ResultFileName);
        string? writeProblem = machine.WriteFile(resultPath, Encoding.UTF8.GetBytes(TraceResultFile.Serialize(new TraceResultFile(outcome, fileName, wprCode))));
        if (writeProblem is not null)
        {
            log.Warn("trace: the result could not be written: " + writeProblem);
        }
        else
        {
            string? grant = machine.GrantRead(resultPath, trayUser);
            if (grant is not null)
            {
                log.Warn("trace: the result could not be made readable: " + grant);
            }
        }

        signals.SignalDone();
        log.Info("trace: " + outcome + (fileName.Length > 0 ? " (" + fileName + ")" : "") + ".");

        if (leaveFolder)
        {
            log.Info("trace: the file is in " + runFolder + " (administrators only), because it could not be made readable. The tray says so.");
            return outcome;
        }

        // The tray reads the result, copies the file if there is one, and says so; the run folder is deleted then. A tray that
        // never says so is gone, and nobody will collect the file, so it is deleted too rather than left holding addresses and
        // maybe pairing keys. A discarded run has nothing for the tray to take. A folder that cannot be deleted holds a copy of
        // the trace, so the run ends with its own exit code and the tray tells the person.
        if (outcome != TraceOutcome.Discarded && !signals.WaitCopied(CopyWait))
        {
            log.Warn(signals.TrayRunning()
                ? "trace: the tray did not say it was finished within " + CopyWait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s, so nobody took the file and it is deleted."
                : "trace: the tray exited before it said it was finished, so nobody took the file and it is deleted.");
        }

        string? deleteProblem = machine.DeleteRunFolder(runFolder);
        if (deleteProblem is not null)
        {
            log.Warn("trace: the run folder was not deleted: " + deleteProblem);
            return fileName.Length > 0 ? TraceOutcome.CopyKept : outcome;
        }

        return outcome;
    }

    // The part between the folder and the result. leaveFolder: nobody is going to collect the file.
    private static TraceOutcome Record(TraceRequest request, ITraceMachine machine, ITraceSignals signals, IProcessRunner runner, ILog log, Func<DateTime> utcNow,
        string runFolder, SecurityIdentifier trayUser, out string fileName, out int wprCode, out bool leaveFolder)
    {
        fileName = "";
        wprCode = 0;
        leaveFolder = false;

        // The profile is written into the private folder and read back from there, so what wpr.exe opens is what was checked.
        byte[] embedded = TraceProfile.ReadEmbedded();
        string profilePath = Path.Combine(runFolder, ProfileFileName);
        if (!TraceProfile.IsExpected(embedded))
        {
            log.Warn("trace: the profile built into this program is not the reviewed one (" + TraceProfile.HashOf(embedded) + ").");
            return TraceOutcome.ProfileChanged;
        }

        string? write = machine.WriteFile(profilePath, embedded);
        byte[]? onDisk = write is null ? machine.ReadFile(profilePath) : null;
        if (write is not null)
        {
            log.Warn("trace: the profile could not be written: " + write);
            return TraceOutcome.SetupFailed;
        }

        if (onDisk is null || !TraceProfile.IsExpected(onDisk))
        {
            log.Warn("trace: the profile on disk is not the reviewed one, so no recording was started.");
            return TraceOutcome.ProfileChanged;
        }

        if (!machine.FileExists(machine.WprPath))
        {
            log.Warn("trace: " + machine.WprPath + " is not there.");
            return TraceOutcome.WprMissing;
        }

        // Stop pressed while the administrator prompt was up: the person wanted no recording, so none is started.
        if (signals.StopAsked())
        {
            log.Info("trace: Stop was pressed before the recording started, so nothing was recorded.");
            return TraceOutcome.StoppedBeforeStart;
        }

        // A session an earlier helper left behind would make -start fail. The cancel names Earshot's own instance, which by
        // wpr's page should keep it to Earshot's own recording (not yet seen in a real one). "No trace running" is the normal
        // answer, so the code is recorded and nothing is concluded from it.
        ProcessRun cancel = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit, runFolder);
        log.Info("trace: wpr -cancel " + Describe(cancel));

        string markerPath = Path.Combine(machine.TraceFolder, MarkerFileName);
        string? marker = machine.WriteFile(markerPath, Encoding.UTF8.GetBytes(TraceMarker.Serialize(new TraceMarker(Environment.ProcessId, request.Nonce, TraceMarker.BootSecondsNow()))));
        if (marker is not null)
        {
            log.Warn("trace: the marker could not be written: " + marker);
        }

        ProcessRun start = runner.Run(machine.WprPath, WprCommand.Start(profilePath, runFolder), StartLimit, runFolder);
        if (!Succeeded(start))
        {
            wprCode = start.ExitCode;
            log.Warn("trace: wpr -start failed: " + Describe(start));

            // A start that was killed at its limit may still have started the session, which must not be left recording. wpr that
            // ran to its end and failed started nothing.
            bool ended = !start.TimedOut;
            if (start.TimedOut || !start.Started)
            {
                ProcessRun cancelAfterStart = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit, runFolder);
                log.Info("trace: wpr -cancel after the failed start " + Describe(cancelAfterStart));
                ended = !start.Started || Succeeded(cancelAfterStart);
            }

            RemoveMarkerIf(ended, machine, markerPath, log);
            return TraceOutcome.StartFailed;
        }

        log.Info("trace: recording. wpr -start " + Describe(start));
        TraceStopReason reason = signals.WaitForStop(MaxRecording);
        log.Info("trace: stopping because " + reason + ".");

        // The tray is gone, so nobody would collect a file: the session is cancelled and nothing is written.
        if (reason == TraceStopReason.TrayExited)
        {
            ProcessRun discard = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit, runFolder);
            log.Info("trace: the tray exited, so the recording was cancelled. wpr -cancel " + Describe(discard));
            RemoveMarkerIf(Succeeded(discard), machine, markerPath, log);
            return TraceOutcome.Discarded;
        }

        fileName = "bluetooth-" + utcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".etl";
        string etlPath = Path.Combine(runFolder, fileName);
        ProcessRun stop = runner.Run(machine.WprPath, WprCommand.Stop(etlPath), StopLimit, runFolder);
        if (!Succeeded(stop))
        {
            wprCode = stop.ExitCode;
            log.Warn("trace: wpr -stop failed: " + Describe(stop));

            // A session that would not save must not be left recording.
            ProcessRun cancelAfter = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit, runFolder);
            log.Info("trace: wpr -cancel after the failed stop " + Describe(cancelAfter));
            RemoveMarkerIf(Succeeded(cancelAfter), machine, markerPath, log);
            fileName = "";
            return TraceOutcome.StopFailed;
        }

        wprCode = stop.ExitCode;
        _ = machine.DeleteFile(markerPath);
        log.Info("trace: wpr -stop " + Describe(stop));

        string? grant = machine.GrantRead(etlPath, trayUser);
        if (grant is not null)
        {
            log.Warn("trace: the saved trace could not be made readable by the tray's user: " + grant);
            leaveFolder = true;
            return TraceOutcome.SavedInPlace;
        }

        return TraceOutcome.Saved;
    }

    // The marker says a session may still be recording. It goes only once a stop or a cancel has worked; otherwise the session
    // may still run, and the next start of the tray says so.
    private static void RemoveMarkerIf(bool sessionEnded, ITraceMachine machine, string markerPath, ILog log)
    {
        if (!sessionEnded)
        {
            log.Warn("trace: the session may still be recording, so the marker is kept and the next start says so.");
            return;
        }

        _ = machine.DeleteFile(markerPath);
    }

    public static int ExitCodeFor(TraceOutcome outcome) => outcome switch
    {
        TraceOutcome.Saved or TraceOutcome.SavedInPlace or TraceOutcome.Discarded or TraceOutcome.StoppedBeforeStart => ExitCodes.Ok,
        TraceOutcome.CopyKept => ExitCodes.IoError,
        TraceOutcome.Refused => ExitCodes.Refused,
        TraceOutcome.WprMissing => ExitCodes.Unavailable,
        _ => ExitCodes.Software,
    };

    private static bool Succeeded(ProcessRun run) => run.Started && !run.TimedOut && run.ExitCode == 0;

    private static string Describe(ProcessRun run)
    {
        if (!run.Started)
        {
            return "did not start: " + run.Problem;
        }

        if (run.TimedOut)
        {
            return "timed out and was ended. " + Tail(run.Output);
        }

        return "returned " + TraceCopy.Encoded(run.ExitCode) + ". " + Tail(run.Output);
    }

    private static string Tail(string output)
    {
        string trimmed = output.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400];
    }
}
