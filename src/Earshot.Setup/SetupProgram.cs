using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Earshot.Setup;

internal sealed partial class WindowsSetupHost : ISetupHost
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

    public string? OwnUserSid
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
    }

    // The account signed in to this session: its user and domain names from the session, turned into a SID.
    // https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw
    public string? SignedInUserSid
    {
        get
        {
            string? user = SessionText(WtsUserName);
            string? domain = SessionText(WtsDomainName);
            if (string.IsNullOrEmpty(user) || domain is null)
            {
                return null;
            }

            try
            {
                return ((SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier))).Value;
            }
            catch (SystemException ex)
            {
                SignedInUserProblem = "The signed-in account could not be read: " + ex.GetType().Name + " (HRESULT 0x" +
                    ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").";
                return null;
            }
        }
    }

    public string SignedInUserProblem { get; private set; } = "";

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const uint WtsCurrentSession = 0xFFFFFFFF;

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    private string? SessionText(int infoClass)
    {
        if (!WTSQuerySessionInformation(0, WtsCurrentSession, infoClass, out nint buffer, out _))
        {
            SignedInUserProblem = "WTSQuerySessionInformation (" + infoClass.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ") failed with error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
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
