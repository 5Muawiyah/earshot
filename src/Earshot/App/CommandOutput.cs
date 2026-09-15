using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Earshot.Interop;

namespace Earshot.App;

// Where probe and diag text goes. Earshot.exe is a Windows-subsystem program with no console of
// its own, so output goes to the file named by --out, to stdout when the caller redirected it, or
// to the console of the process that started Earshot.
// https://learn.microsoft.com/en-us/windows/console/attachconsole
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
        [NotNullWhen(false)] out string? problem)
    {
        if (outPath is not null)
        {
            return TryOpenFile(outPath, out output, out problem);
        }

        // A redirected stdout already works; attaching to a console is only needed when it is not.
        if (Console.IsOutputRedirected)
        {
            output = new CommandOutput(Console.Out, ownsWriter: false, "stdout");
            problem = null;
            return true;
        }

        bool attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        int error = attached ? 0 : Marshal.GetLastPInvokeError();
        if (attached || error == NativeMethods.ERROR_ACCESS_DENIED)
        {
            TextWriter writer = Console.Out;
            if (attached)
            {
                // The shell has already printed its prompt; start on a fresh line.
                writer.WriteLine();
            }

            output = new CommandOutput(writer, ownsWriter: false, "console");
            problem = null;
            return true;
        }

        output = null;
        problem = "There is no console to write to (AttachConsole failed with Win32 error " +
                  error.ToString(CultureInfo.InvariantCulture) + "). Run it from a terminal or pass --out <path>.";
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
