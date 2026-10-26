using System.Globalization;
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

    // Gives the user who runs the tray read access to one file. Null when done; otherwise why not.
    string? GrantRead(string path, int trayPid);
}

// The tray's three events and the tray process, as the helper sees them.
internal interface ITraceSignals
{
    // Opens the events the tray made. Null when all are open; otherwise why not.
    string? Open();

    bool TrayRunning();

    // Blocks until the tray asks to stop, the tray process exits, or max passes.
    TraceStopReason WaitForStop(TimeSpan max);

    void SignalDone();

    // True when the tray said it has finished with the run folder.
    bool WaitCopied(TimeSpan max);
}

// The elevated half of "Record a Bluetooth trace": one administrator prompt, the whole recording inside it.
//
//   trace <tray pid> <nonce>
//
// It starts the Bluetooth stack's verbose Windows Performance Recorder profile, waits for the tray to say stop (or for the
// tray to exit, or for ten minutes), saves the trace into a folder only administrators can open, lets the tray's user read
// the file, and waits for the tray to copy it before it deletes its own copy. It never writes into a folder the user can
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
    internal const string MarkerFileName = "running.txt";

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

        string? folderProblem = machine.PrepareRunFolder(request.Nonce, out string runFolder);
        if (folderProblem is not null)
        {
            log.Warn("trace: the private folder could not be made, so nothing was started: " + folderProblem);
            signals.SignalDone();
            return TraceOutcome.SetupFailed;
        }

        TraceOutcome outcome = Record(request, machine, signals, runner, log, utcNow, runFolder, out string fileName, out int wprCode, out bool leaveFolder);

        string resultPath = Path.Combine(runFolder, ResultFileName);
        string? writeProblem = machine.WriteFile(resultPath, Encoding.UTF8.GetBytes(TraceResultFile.Serialize(new TraceResultFile(outcome, fileName, wprCode))));
        if (writeProblem is not null)
        {
            log.Warn("trace: the result could not be written: " + writeProblem);
        }
        else
        {
            string? grant = machine.GrantRead(resultPath, request.TrayPid);
            if (grant is not null)
            {
                log.Warn("trace: the result could not be made readable: " + grant);
            }
        }

        signals.SignalDone();
        log.Info("trace: " + outcome + (fileName.Length > 0 ? " (" + fileName + ")" : "") + ".");

        if (leaveFolder)
        {
            log.Info("trace: the file is in " + runFolder + " (administrators only). The tray did not take it.");
            return outcome;
        }

        // The tray reads the result, copies the file if there is one, and says so. Only then is the run folder deleted. A folder
        // that stays holds a copy of the trace, so the run ends with its own exit code and the tray tells the person.
        if (signals.WaitCopied(CopyWait))
        {
            string? deleteProblem = machine.DeleteRunFolder(runFolder);
            if (deleteProblem is not null)
            {
                log.Warn("trace: the run folder was not deleted: " + deleteProblem);
                return fileName.Length > 0 ? TraceOutcome.CopyKept : outcome;
            }
        }
        else
        {
            log.Warn("trace: the tray did not say it was finished within " + CopyWait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s, so the folder was left in place (the file was not copied): " + runFolder);
        }

        return outcome;
    }

    // The part between the folder and the result. leaveFolder: nobody is going to collect the file.
    private static TraceOutcome Record(TraceRequest request, ITraceMachine machine, ITraceSignals signals, IProcessRunner runner, ILog log, Func<DateTime> utcNow,
        string runFolder, out string fileName, out int wprCode, out bool leaveFolder)
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

        // A session an earlier helper left behind would make -start fail. The cancel names Earshot's own instance, so it cannot
        // end another program's recording. "No trace running" is the normal answer, so the code is recorded and nothing is
        // concluded from it.
        ProcessRun cancel = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit);
        log.Info("trace: wpr -cancel " + Describe(cancel));

        string markerPath = Path.Combine(machine.TraceFolder, MarkerFileName);
        string? marker = machine.WriteFile(markerPath, Encoding.UTF8.GetBytes(TraceMarker.Serialize(new TraceMarker(Environment.ProcessId, request.Nonce))));
        if (marker is not null)
        {
            log.Warn("trace: the marker could not be written: " + marker);
        }

        ProcessRun start = runner.Run(machine.WprPath, WprCommand.Start(profilePath, runFolder), StartLimit);
        if (!Succeeded(start))
        {
            wprCode = start.ExitCode;
            log.Warn("trace: wpr -start failed: " + Describe(start));
            _ = machine.DeleteFile(markerPath);
            return TraceOutcome.StartFailed;
        }

        log.Info("trace: recording. wpr -start " + Describe(start));
        TraceStopReason reason = signals.WaitForStop(MaxRecording);
        log.Info("trace: stopping because " + reason + ".");

        fileName = "bluetooth-" + utcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".etl";
        string etlPath = Path.Combine(runFolder, fileName);
        ProcessRun stop = runner.Run(machine.WprPath, WprCommand.Stop(etlPath), StopLimit);
        if (!Succeeded(stop))
        {
            wprCode = stop.ExitCode;
            log.Warn("trace: wpr -stop failed: " + Describe(stop));

            // A session that would not save must not be left recording.
            ProcessRun cancelAfter = runner.Run(machine.WprPath, WprCommand.Cancel, CancelLimit);
            log.Info("trace: wpr -cancel after the failed stop " + Describe(cancelAfter));
            _ = machine.DeleteFile(markerPath);
            fileName = "";
            return TraceOutcome.StopFailed;
        }

        wprCode = stop.ExitCode;
        _ = machine.DeleteFile(markerPath);
        log.Info("trace: wpr -stop " + Describe(stop));

        string? grant = machine.GrantRead(etlPath, request.TrayPid);
        if (grant is not null)
        {
            log.Warn("trace: the saved trace could not be made readable by the tray's user: " + grant);
            leaveFolder = true;
            return TraceOutcome.SavedInPlace;
        }

        if (reason == TraceStopReason.TrayExited)
        {
            leaveFolder = true;
            return TraceOutcome.SavedInPlace;
        }

        return TraceOutcome.Saved;
    }

    public static int ExitCodeFor(TraceOutcome outcome) => outcome switch
    {
        TraceOutcome.Saved or TraceOutcome.SavedInPlace => ExitCodes.Ok,
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
