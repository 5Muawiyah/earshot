using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Earshot.Tracing;

// Started: false when the program could not be started (Problem says why). ExitCode is the program's raw exit code.
internal sealed record ProcessRun(bool Started, int ExitCode, bool TimedOut, string Output, string? Problem);

// The one boundary the trace code starts programs through, so tests fake it and one test runs the real one.
internal interface IProcessRunner
{
    // tempFolder: when given (the trace's elevated wpr runs), the program's TEMP and TMP, and the program gets only the system's
    // own folders as its environment.
    ProcessRun Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, string? tempFolder = null);
}

// CreateProcess without a shell, no window, arguments passed as a list (no command-line parsing of a path), standard input
// closed at once so a program that waits for a key ends, output read, and a run past its limit killed with its children.
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill#system-diagnostics-process-kill(system-boolean)
internal sealed class WindowsProcessRunner : IProcessRunner
{
    private const int MaxOutputCharacters = 8000;

    // The system folders an elevated run's PATH holds, in this order.
    internal static IReadOnlyList<string> SystemPath()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return [Environment.SystemDirectory, windows, Path.Combine(Environment.SystemDirectory, "Wbem")];
    }

    internal static IReadOnlyList<(string Name, string Value)> ElevatedEnvironment(string tempFolder)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            ("SystemRoot", windows),
            ("windir", windows),
            ("SystemDrive", Path.GetPathRoot(windows)?.TrimEnd('\\') ?? ""),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("ALLUSERSPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("ComSpec", Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            ("PATH", string.Join(";", SystemPath())),
            ("PATHEXT", ".COM;.EXE;.BAT;.CMD"),
            ("TEMP", tempFolder),
            ("TMP", tempFolder),
        ];
    }

    // How long the redirected output may take to end once the program has: another process can hold the inherited pipes open
    // after it, and the elevated helper must not wait on that for ever.
    internal static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    public ProcessRun Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, string? tempFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentNullException.ThrowIfNull(args);

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        // An elevated wpr run starts with an environment of the system's own folders, read from Windows rather than from the
        // person's variables (HKCU\Environment, PATH), which any program of theirs can change: nothing of theirs reaches an
        // elevated program. TEMP and TMP are the private run folder.
        // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.environment
        if (tempFolder is not null)
        {
            info.Environment.Clear();
            foreach ((string name, string value) in ElevatedEnvironment(tempFolder))
            {
                info.Environment[name] = value;
            }
        }

        var output = new StringBuilder();
        using var outputEnded = new ManualResetEventSlim();
        using var errorEnded = new ManualResetEventSlim();
        void Append(string? line, ManualResetEventSlim ended)
        {
            // A null line is the end of that stream.
            if (line is null)
            {
                ended.Set();
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputCharacters)
                {
                    output.AppendLine(line);
                }
            }
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRun(false, ex.NativeErrorCode, false, "", exe + ": " + ex.Message + " (" + ex.NativeErrorCode + ")");
        }
        catch (InvalidOperationException ex)
        {
            return new ProcessRun(false, 0, false, "", exe + ": " + ex.Message);
        }

        if (process is null)
        {
            return new ProcessRun(false, 0, false, "", exe + ": no process was started.");
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => Append(e.Data, outputEnded);
            process.ErrorDataReceived += (_, e) => Append(e.Data, errorEnded);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                string? killedProblem = process.WaitForExit(DrainLimit)
                    ? Drain(process, outputEnded, errorEnded)
                    : "it was asked to end and had not ended " + DrainLimit.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s later.";
                return new ProcessRun(true, -1, true, Text(output), killedProblem);
            }

            string? problem = Drain(process, outputEnded, errorEnded);
            return new ProcessRun(true, process.ExitCode, false, Text(output), problem);
        }
    }

    // Waits for both redirected streams to end, within DrainLimit in all; past it the run stops waiting, and the output may be
    // short. A stream that ends after that calls Set on a disposed event, which does not throw (ProcessRunnerTests). Two single waits, not WaitHandle.WaitAll, which throws on an STA thread such as the helper's (Main is STAThread).
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.waithandle.waitall
    private static string? Drain(Process process, ManualResetEventSlim outputEnded, ManualResetEventSlim errorEnded)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan left = DrainLimit;
        if (outputEnded.Wait(left))
        {
            left = DrainLimit - watch.Elapsed;
            if (errorEnded.Wait(left < TimeSpan.Zero ? TimeSpan.Zero : left))
            {
                return null;
            }
        }

        process.CancelOutputRead();
        process.CancelErrorRead();
        return "the program's output did not end within " + DrainLimit.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
            " s of it ending (another process may hold its pipes), so it may be short.";
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
