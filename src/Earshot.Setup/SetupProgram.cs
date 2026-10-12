using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Earshot.Setup;

internal sealed class WindowsSetupHost : ISetupHost
{
    public bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string MachineFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Earshot");

    public string InstalledExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Earshot", "Earshot.exe");
}

internal static partial class SetupProgram
{
    // LOAD_LIBRARY_SEARCH_SYSTEM32: later DLL loads search System32 only, never the folder Setup was started from (a download
    // folder anyone can write to) or the current directory.
    // https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories
    internal const uint LoadLibrarySearchSystem32 = 0x00000800;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [STAThread]
    private static int Main()
    {
        // First statement: nothing is loaded before this restriction is in place.
        if (!SetDefaultDllDirectories(LoadLibrarySearchSystem32))
        {
            int error = Marshal.GetLastPInvokeError();
            NativeTaskDialog.Message(NativeTaskDialog.IconError, SetupText.WindowTitle, "Setup cannot start safely",
                "Windows would not limit where Setup loads code from (code " + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            return SetupExit.Failed;
        }

        try
        {
            var flow = new SetupFlow(new EmbeddedPayload(), new NtfsFolderAcl(), new WindowsProcessLauncher(), new TaskDialogUi(), new WindowsSetupHost());
            return flow.Run().ExitCode;
        }
        catch (InvalidOperationException ex)
        {
            // The dialog itself failed, so another cannot be relied on; this is the last thing the person sees.
            NativeTaskDialog.Message(NativeTaskDialog.IconError, SetupText.WindowTitle, SetupText.FailedInstruction, ex.Message);
            return SetupExit.Failed;
        }
    }
}
