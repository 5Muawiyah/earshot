using System.Security.Principal;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;

namespace Earshot.TestWindow;

// Entry point. Every refusal is decided by StartupGate.Evaluate, a pure function over facts
// gathered here, so the decision itself can be tested without an elevated process, a second real
// instance or a machine missing Windows PowerShell 5.1.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --render <folder>: draws the window's views to PNG files from made-up data and exits. It starts no test and reads
        // no device, and it refuses to run unless the caller says it is on a private desktop (EARSHOT_RENDER_ON_PRIVATE_DESKTOP=1).
        int renderAt = Array.IndexOf(args, TesterPictures.FlagName);
        if (renderAt >= 0)
        {
            return RenderPictures(renderAt + 1 < args.Length ? args[renderAt + 1] : null);
        }

        // --sandbox's presence and its actual, valid folder are decided together, once, here.
        // sandboxRequested (used both by the refusal below and to build the real SandboxOptions
        // MainForm gets) is true only when a valid folder was actually parsed; the switch present
        // with nothing usable after it is its own refusal, never a silent fall-through to a REAL,
        // non-sandboxed run with the safety-variable refusal already bypassed.
        bool sandboxArgumentPresent = StartupGate.HasSandboxArgument(args);
        bool sandboxParsed = SandboxOptions.TryParse(args, out SandboxOptions? sandbox);
        bool sandboxRequested = sandboxArgumentPresent && sandboxParsed;
        bool sandboxArgumentWithoutValidFolder = sandboxArgumentPresent && !sandboxParsed;
        bool safeModeSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EARSHOT_SAFE_MODE"));
        bool dataRootSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EARSHOT_DATA_ROOT"));
        bool solutionFound = StartupGate.FindSolutionAbove(AppContext.BaseDirectory, out string? repoRoot);
        bool powerShell51Found = File.Exists(PowerShell51.ExecutablePath());

        using Mutex instanceMutex = StartupGate.TryAcquireSingleInstance(out bool acquiredInstance);

        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: IsRunningElevated(),
            safeModeVariableSet: safeModeSet,
            dataRootVariableSet: dataRootSet,
            sandboxRequested: sandboxRequested,
            sandboxArgumentWithoutValidFolder: sandboxArgumentWithoutValidFolder,
            anotherInstanceRunning: !acquiredInstance,
            solutionFound: solutionFound,
            powerShell51Found: powerShell51Found,
            sandboxFolderInsideProtectedRoot: sandboxArgumentWithoutValidFolder && StartupGate.IsSandboxFolderInsideProtectedRoot(args));

        if (refusal != StartupRefusal.None)
        {
            MessageBox.Show(StartupGate.Message(refusal), "Earshot live tests", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            return 1;
        }

        // The mutex stays owned by this process for as long as the window runs, so a second
        // instance's own attempt to create it sees createdNew false the whole time. It is
        // released implicitly when the handle closes at process exit.
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Manifest.DefaultPath());
        IReadOnlyList<WordingEntry> wording = Wording.Load(Wording.DefaultPath());
        string exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Earshot", "Earshot.exe");

        Application.Run(new MainForm(repoRoot!, rows, wording, sandbox, exePath));
        return 0;
    }

    // Exit 0 when the pictures are written, 1 for a missing folder, 2 when the desktop is not a private one. No message box
    // for either: a box shown on the desktop in use is what the refusal exists to prevent.
    private static int RenderPictures(string? folder)
    {
        if (!TesterPictures.CallerSaysPrivateDesktop())
        {
            return 2;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return 1;
        }

        TesterPictures.RenderAll(folder, TesterPictures.CallerSaysPrivateDesktop);
        return 0;
    }

    // False when the token cannot be read, which is never mistaken for elevation.
    // https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsprincipal.isinrole
    private static bool IsRunningElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
