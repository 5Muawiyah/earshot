using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Where probe and diag output goes when no --out is given. Earshot.exe is a Windows-subsystem program,
// so a terminal without redirection gives it no standard output handle at all; the output must then
// go to the parent's console, attached before Console.Out is first read.
[TestClass]
public sealed class CommandOutputTests
{
    private const int ErrorInvalidHandle = 6;
    private const long ConsoleHandle = 0x54;
    private const long FileHandle = 0x58;

    private static readonly string[] ExpectedFixLines = ["fix output: Fix connection plan:", "fix output: ", "fix output:   1. nothing to change"];
    private static readonly string[] AttachThenOut = ["handle", "attach", "out"];
    private static readonly string[] TypeThenOut = ["handle", "type", "out"];
    private static readonly string[] TypeThenAttachThenOut = ["handle", "type", "attach", "out"];

    private sealed class FakeConsoleHost : IConsoleHost
    {
        private readonly TextWriter _out;

        public FakeConsoleHost(nint handle, uint fileType = NativeMethods.FILE_TYPE_UNKNOWN, int attachError = 0, TextWriter? output = null)
        {
            Handle = handle;
            Type = fileType;
            AttachError = attachError;
            _out = output ?? new StringWriter(CultureInfo.InvariantCulture);
        }

        public nint Handle { get; }

        public uint Type { get; }

        public int AttachError { get; }

        public List<string> Calls { get; } = [];

        public TextWriter Out
        {
            get
            {
                Calls.Add("out");
                return _out;
            }
        }

        public nint StandardOutputHandle()
        {
            Calls.Add("handle");
            return Handle;
        }

        public uint FileType(nint handle)
        {
            Assert.AreEqual(Handle, handle);
            Calls.Add("type");
            return Type;
        }

        public bool AttachToParentConsole(out int error)
        {
            Calls.Add("attach");
            error = AttachError;
            return AttachError == 0;
        }
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void NoStandardOutputHandleAttachesToTheParentConsoleBeforeReadingConsoleOut(long handle)
    {
        var host = new FakeConsoleHost((nint)handle);

        Assert.IsTrue(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            CollectionAssert.AreEqual(AttachThenOut, host.Calls);
            Assert.AreEqual("console", output.Destination);
            Assert.AreEqual(Environment.NewLine, output.Writer.ToString(), "The shell prompt is already on the line.");
        }
    }

    [TestMethod]
    [DataRow(NativeMethods.FILE_TYPE_DISK)]
    [DataRow(NativeMethods.FILE_TYPE_PIPE)]
    public void AFileOrPipeIsUsedAsStdoutWithoutAttaching(uint fileType)
    {
        var host = new FakeConsoleHost((nint)FileHandle, fileType);

        Assert.IsTrue(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            CollectionAssert.AreEqual(TypeThenOut, host.Calls);
            Assert.AreEqual("stdout", output.Destination);
            Assert.AreEqual("", output.Writer.ToString());
        }
    }

    [TestMethod]
    [DataRow(NativeMethods.FILE_TYPE_CHAR)]
    [DataRow(NativeMethods.FILE_TYPE_UNKNOWN)]
    [DataRow(NativeMethods.FILE_TYPE_REMOTE)]
    public void AnyOtherHandleTypeAttachesFirst(uint fileType)
    {
        var host = new FakeConsoleHost((nint)ConsoleHandle, fileType);

        Assert.IsTrue(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            CollectionAssert.AreEqual(TypeThenAttachThenOut, host.Calls);
            Assert.AreEqual("console", output.Destination);
        }
    }

    [TestMethod]
    public void AnAlreadyAttachedConsoleIsUsed()
    {
        var host = new FakeConsoleHost(0, attachError: NativeMethods.ERROR_ACCESS_DENIED);

        Assert.IsTrue(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            Assert.AreEqual("console", output.Destination);
            Assert.AreEqual("", output.Writer.ToString());
        }
    }

    [TestMethod]
    public void NoHandleAndNoParentConsoleIsReportedNotDiscarded()
    {
        var host = new FakeConsoleHost(0, attachError: ErrorInvalidHandle);

        Assert.IsFalse(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem));

        Assert.IsNull(output);
        Assert.IsTrue(problem.Contains("ERROR_INVALID_HANDLE", StringComparison.Ordinal), problem);
        Assert.IsTrue(problem.Contains("--out", StringComparison.Ordinal), problem);
        CollectionAssert.DoesNotContain(host.Calls, "out");
    }

    [TestMethod]
    public void ACharacterDeviceWithoutAParentConsoleIsStillWritten()
    {
        var host = new FakeConsoleHost((nint)ConsoleHandle, NativeMethods.FILE_TYPE_CHAR, attachError: ErrorInvalidHandle);

        Assert.IsTrue(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            Assert.AreEqual("stdout", output.Destination);
        }
    }

    [TestMethod]
    [DataRow(0L, NativeMethods.FILE_TYPE_UNKNOWN, 0)]
    [DataRow(FileHandle, NativeMethods.FILE_TYPE_PIPE, 0)]
    [DataRow(ConsoleHandle, NativeMethods.FILE_TYPE_CHAR, ErrorInvalidHandle)]
    public void AWriterThatDiscardsEverythingIsReported(long handle, uint fileType, int attachError)
    {
        var host = new FakeConsoleHost((nint)handle, fileType, attachError, TextWriter.Null);

        Assert.IsFalse(CommandOutput.TryOpen(null, host, out CommandOutput? output, out string? problem));

        Assert.IsNull(output);
        Assert.IsTrue(problem.Contains("cannot be written to", StringComparison.Ordinal), problem);
    }

    // Fix connection started from the tray or from Setup.exe: both are Windows-subsystem programs with no console, so the child
    // has no standard output and no parent console to attach to (AttachConsole fails with ERROR_INVALID_HANDLE).
    [TestMethod]
    public void WithNoConsoleAndALogFallbackTheOutputGoesToTheLog()
    {
        var host = new FakeConsoleHost(0, attachError: ErrorInvalidHandle);
        var log = new CapturingLog();

        Assert.IsTrue(CommandOutput.TryOpen(null, host, new LogFallback(log, "fix output: "), out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            Assert.AreEqual("log", output.Destination);
            output.Writer.WriteLine("Fix connection plan:");
            output.Writer.WriteLine();
            output.Writer.Write("  1. nothing to change");
        }

        string[] lines = [.. log.Entries.Select(e => e.Message)];
        CollectionAssert.AreEqual(ExpectedFixLines, lines);
        Assert.IsTrue(log.Entries.All(e => e.Level == LogLevel.Info));
        CollectionAssert.DoesNotContain(host.Calls, "out");
    }

    [TestMethod]
    public void WithALogFallbackAWriterThatDiscardsEverythingGoesToTheLogToo()
    {
        var host = new FakeConsoleHost((nint)FileHandle, NativeMethods.FILE_TYPE_PIPE, 0, TextWriter.Null);
        var log = new CapturingLog();

        Assert.IsTrue(CommandOutput.TryOpen(null, host, new LogFallback(log, "fix output: "), out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            Assert.AreEqual("log", output.Destination);
            output.Writer.WriteLine("kept");
        }

        Assert.IsTrue(log.Has(LogLevel.Info, "fix output: kept"));
    }

    [TestMethod]
    public void ALogFallbackIsNotUsedWhenAConsoleCanBeWrittenTo()
    {
        var host = new FakeConsoleHost(0);
        var log = new CapturingLog();

        Assert.IsTrue(CommandOutput.TryOpen(null, host, new LogFallback(log, "fix output: "), out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            Assert.AreEqual("console", output.Destination);
            output.Writer.WriteLine("to the console");
        }

        Assert.IsEmpty(log.Entries);
    }

    [TestMethod]
    public void AnOutPathBeatsTheLogFallback()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();

        Assert.IsTrue(CommandOutput.TryOpen(temp.File("fix.txt"), new FakeConsoleHost(0), new LogFallback(log, "fix output: "), out CommandOutput? output, out string? problem), problem);

        using (output)
        {
            output.Writer.WriteLine("to the file");
        }

        Assert.AreEqual("to the file" + Environment.NewLine, File.ReadAllText(temp.File("fix.txt")));
        Assert.IsEmpty(log.Entries);
    }

    [TestMethod]
    public void AnOutPathNeverTouchesTheConsole()
    {
        using var temp = new TempFolder();
        var host = new FakeConsoleHost(0);

        Assert.IsTrue(CommandOutput.TryOpen(temp.File("probe.txt"), host, out CommandOutput? output, out string? problem), problem);

        output.Dispose();
        Assert.IsEmpty(host.Calls);
    }
}
