using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Earshot.Tracing;

// Started: false when the program could not be started (Problem says why). ExitCode is the program's raw exit code.
internal sealed record ProcessRun(bool Started, int ExitCode, bool TimedOut, string Output, string? Problem);

// The one boundary the trace code starts programs through, so tests fake it and one test runs the real one.
internal interface IProcessRunner
{
    ProcessRun Run(string exe, IReadOnlyList<string> args, TimeSpan timeout);
}

// CreateProcess without a shell, no window, arguments passed as a list (no command-line parsing of a path), standard input
// closed at once so a program that waits for a key ends, output read, and a run past its limit killed with its children.
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill#system-diagnostics-process-kill(system-boolean)
internal sealed class WindowsProcessRunner : IProcessRunner
{
    private const int MaxOutputCharacters = 8000;

    public ProcessRun Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
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
