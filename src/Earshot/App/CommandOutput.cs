using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.App;

// Where probe and diag text goes: the file named by --out, stdout when the caller redirected it to a
// file or a pipe, and otherwise the console of the process that started Earshot.
//
// Earshot.exe is a Windows-subsystem program. Started from a terminal without redirection it has no
// standard output handle at all: GetStdHandle returns NULL. .NET then reports stdout as redirected
// and Console.Out discards everything, so neither can be asked first. The handle is checked instead.
// Only a disk file or a pipe counts as redirected. With no handle, an invalid one or a character
// device, Earshot attaches to the parent's console before Console.Out is first read, because
// attaching replaces a NULL or console standard handle with a handle to that console, and .NET opens
// stdout once, on first use.
// https://learn.microsoft.com/en-us/windows/console/getstdhandle
// https://learn.microsoft.com/en-us/windows/console/attachconsole
// https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletype
internal sealed class CommandOutput : IDisposable
{
    private readonly bool _ownsWriter;

    private CommandOutput(TextWriter writer, bool ownsWriter, string destination)
    {
        Writer = writer;
        _ownsWriter = ownsWriter;
        Destination = destination;
    }

    public TextWriter Writer { get; }

    // "console", "stdout" or the full path of the output file.
    public string Destination { get; }

    public static bool TryOpen(
        string? outPath,
        [NotNullWhen(true)] out CommandOutput? output,
        [NotNullWhen(false)] out string? problem) =>
        TryOpen(outPath, SystemConsoleHost.Instance, out output, out problem);

    internal static bool TryOpen(
        string? outPath,
        IConsoleHost host,
        [NotNullWhen(true)] out CommandOutput? output,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (outPath is not null)
        {
            return TryOpenFile(outPath, out output, out problem);
        }

        nint handle = host.StandardOutputHandle();
        bool hasHandle = handle != 0 && handle != NativeMethods.INVALID_HANDLE_VALUE;
        if (hasHandle && host.FileType(handle) is NativeMethods.FILE_TYPE_DISK or NativeMethods.FILE_TYPE_PIPE)
        {
            return TryUseStandardOutput(host, "stdout", startOnNewLine: false, out output, out problem);
        }

        if (host.AttachToParentConsole(out int error))
        {
            // The shell has already printed its prompt; start on a fresh line.
            return TryUseStandardOutput(host, "console", startOnNewLine: true, out output, out problem);
        }

        if (error == NativeMethods.ERROR_ACCESS_DENIED)
        {
            // Already attached to a console.
            return TryUseStandardOutput(host, "console", startOnNewLine: false, out output, out problem);
        }

        if (hasHandle)
        {
            // A character device that is not a console, such as NUL, and no parent console.
            return TryUseStandardOutput(host, "stdout", startOnNewLine: false, out output, out problem);
        }

        output = null;
        problem = "There is no console to write to (AttachConsole failed with " +
                  NativeCodes.Win32(unchecked((uint)error)) + "). Run it from a terminal or pass --out <path>.";
        return false;
    }

    public void Dispose()
    {
        Writer.Flush();
        if (_ownsWriter)
        {
            Writer.Dispose();
        }
    }

    // Console.Out is TextWriter.Null when .NET found no usable standard output handle. Writing to it
    // would lose the output without a trace, so that is reported instead.
    private static bool TryUseStandardOutput(
        IConsoleHost host,
        string destination,
        bool startOnNewLine,
        [NotNullWhen(true)] out CommandOutput? output,
        [NotNullWhen(false)] out string? problem)
    {
        TextWriter writer = host.Out;
        if (ReferenceEquals(writer, TextWriter.Null))
        {
            output = null;
            problem = "Standard output (" + destination + ") cannot be written to. Pass --out <path>.";
            return false;
        }

        if (startOnNewLine)
        {
            writer.WriteLine();
        }

        output = new CommandOutput(writer, ownsWriter: false, destination);
        problem = null;
        return true;
    }

    private static bool TryOpenFile(
        string outPath,
        [NotNullWhen(true)] out CommandOutput? output,
        [NotNullWhen(false)] out string? problem)
    {
        output = null;
        try
        {
            string fullPath = Path.GetFullPath(outPath);
            string? folder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var writer = new StreamWriter(fullPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output = new CommandOutput(writer, ownsWriter: true, fullPath);
            problem = null;
            return true;
        }
        catch (IOException ex)
        {
            problem = "Could not open " + outPath + ": " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = "Could not open " + outPath + ": " + ex.Message;
        }
        catch (ArgumentException ex)
        {
            problem = "Could not open " + outPath + ": " + ex.Message;
        }
        catch (NotSupportedException ex)
        {
            problem = "Could not open " + outPath + ": " + ex.Message;
        }

        return false;
    }
}

// The process's standard output and console, behind a seam so each handle case can be tested.
internal interface IConsoleHost
{
    // GetStdHandle(STD_OUTPUT_HANDLE): NULL when the process has none, INVALID_HANDLE_VALUE on failure.
    nint StandardOutputHandle();

    // GetFileType for the handle: one of the FILE_TYPE_* values.
    uint FileType(nint handle);

    // AttachConsole(ATTACH_PARENT_PROCESS). On failure, error is the Win32 error.
    bool AttachToParentConsole(out int error);

    // Console.Out. Read only once the attach decision is made.
    TextWriter Out { get; }
}

internal sealed class SystemConsoleHost : IConsoleHost
{
    public static readonly SystemConsoleHost Instance = new();

    private SystemConsoleHost()
    {
    }

    public TextWriter Out => Console.Out;

    public nint StandardOutputHandle() => NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);

    public uint FileType(nint handle) => NativeMethods.GetFileType(handle);

    public bool AttachToParentConsole(out int error)
    {
        if (NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }
}
