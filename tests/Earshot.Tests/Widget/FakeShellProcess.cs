using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A second process that plays Explorer on a private desktop: it registers a window of the taskbar's class
// (Shell_TrayWnd or Shell_SecondaryTrayWnd), topmost, over the given rectangle, and puts it back on top of the topmost
// band at a fixed interval the way the shell does for its taskbar. The gauge's z-order rules are only honest against a
// window of another process, which is why this is a real child and not a window of the test's own. The child is started
// on the given desktop and killed on Dispose. Nothing here touches the owner's desktop.
internal sealed class FakeShellProcess : IDisposable
{
    // WS_POPUP | WS_VISIBLE and WS_EX_TOPMOST | WS_EX_TOOLWINDOW, then the raise the shell does: SetWindowPos
    // (HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE).
    private const string Script = """
        param([string]$Class, [string]$Bounds, [string]$HandleFile, [int]$RaiseEveryMs, [int]$Seconds)
        Add-Type @"
        using System;
        using System.Runtime.InteropServices;
        public static class FS {
          [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
          public struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; }
          [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
          [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS c);
          [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
          [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
          [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
          [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint remove);
          [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
          [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
          [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string n);
          delegate IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l);
          static Proc keep;
          public static void Run(string cls, int x, int y, int w, int h, string handleFile, int raiseEveryMs, int seconds) {
            keep = (hh, m, ww, ll) => DefWindowProcW(hh, m, ww, ll);
            var c = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(keep), hInstance = GetModuleHandleW(null), lpszClassName = cls };
            RegisterClassW(ref c);
            IntPtr hwnd = CreateWindowExW(0x88, cls, "", 0x90000000, x, y, w, h, IntPtr.Zero, IntPtr.Zero, c.hInstance, IntPtr.Zero);
            System.IO.File.WriteAllText(handleFile, hwnd.ToInt64().ToString());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long next = raiseEveryMs;
            while (sw.ElapsedMilliseconds < seconds * 1000L) {
              MSG msg;
              while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
              if (raiseEveryMs > 0 && sw.ElapsedMilliseconds >= next) {
                SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                next += raiseEveryMs;
              }
              System.Threading.Thread.Sleep(2);
            }
          }
        }
        "@
        $b = $Bounds.Split(',') | ForEach-Object { [int]$_ }
        [FS]::Run($Class, $b[0], $b[1], $b[2], $b[3], $HandleFile, $RaiseEveryMs, $Seconds)
        """;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? application, char[] commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, char[] info, int length, out int needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private readonly ProcessInformation _process;
    private readonly string _scriptFile;
    private readonly string _handleFile;
    private bool _disposed;

    private FakeShellProcess(ProcessInformation process, string scriptFile, string handleFile, nint handle)
    {
        _process = process;
        _scriptFile = scriptFile;
        _handleFile = handleFile;
        Handle = handle;
    }

    // The window the child made.
    public nint Handle { get; }

    // Starts the child on desktop (the handle CardDesktop.Run hands its work) and waits until its window exists.
    // raiseEveryMs of 0 never raises it.
    public static FakeShellProcess Start(nint desktop, string windowClass, Rectangle bounds, int raiseEveryMs, int seconds)
    {
        var name = new char[256];
        if (!GetUserObjectInformationW(desktop, 2, name, name.Length * sizeof(char), out _))
        {
            throw new AssertFailedException("GetUserObjectInformation failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ".");
        }

        string stamp = Guid.NewGuid().ToString("N");
        string scriptFile = Path.Combine(Path.GetTempPath(), "earshot-fakeshell-" + stamp + ".ps1");
        string handleFile = Path.Combine(Path.GetTempPath(), "earshot-fakeshell-" + stamp + ".txt");
        File.WriteAllText(scriptFile, Script);

        string rectangle = string.Create(CultureInfo.InvariantCulture, $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
        string command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + scriptFile + "\" -Class " + windowClass +
            " -Bounds " + rectangle + " -HandleFile \"" + handleFile + "\" -RaiseEveryMs " +
            raiseEveryMs.ToString(CultureInfo.InvariantCulture) + " -Seconds " + seconds.ToString(CultureInfo.InvariantCulture);
        var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = "WinSta0\\" + new string(name).TrimEnd('\0') };
        const uint CreateNoWindow = 0x08000000;
        if (!CreateProcessW(null, (command + "\0").ToCharArray(), 0, 0, false, CreateNoWindow, 0, null, ref startup, out ProcessInformation process))
        {
            throw new AssertFailedException("CreateProcess failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ".");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string text = "";
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (File.Exists(handleFile))
            {
                try
                {
                    text = File.ReadAllText(handleFile);
                }
                catch (IOException)
                {
                    text = "";
                }

                if (text.Length > 0)
                {
                    break;
                }
            }

            Thread.Sleep(50);
        }

        if (text.Length == 0 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long handle) || handle == 0)
        {
            TerminateProcess(process.Process, 1);
            CloseHandle(process.Process);
            CloseHandle(process.Thread);
            throw new AssertFailedException("The fake shell process did not make its window.");
        }

        Thread.Sleep(150);
        return new FakeShellProcess(process, scriptFile, handleFile, (nint)handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TerminateProcess(_process.Process, 0);
        CloseHandle(_process.Process);
        CloseHandle(_process.Thread);
        File.Delete(_scriptFile);
        File.Delete(_handleFile);
    }
}
