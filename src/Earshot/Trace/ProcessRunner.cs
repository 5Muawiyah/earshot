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
        void Append(string? line)
        {
            if (line is null)
            {
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
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return new ProcessRun(true, -1, true, Text(output), null);
            }

            // The no-argument overload waits for the redirected streams to end, so every line is in Output.
            process.WaitForExit();
            return new ProcessRun(true, process.ExitCode, false, Text(output), null);
        }
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
