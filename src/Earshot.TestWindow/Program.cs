using System.Security.Principal;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;

namespace Earshot.TestWindow;

// Entry point. Every refusal in design.md section 8.1 is decided by StartupGate.Evaluate, a pure
// function over facts gathered here, so the decision itself can be tested without an elevated
// process, a second real instance or a machine missing Windows PowerShell 5.1.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool sandboxRequested = StartupGate.HasSandboxArgument(args);
        bool safeModeSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EARSHOT_SAFE_MODE"));
        bool dataRootSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EARSHOT_DATA_ROOT"));
        bool solutionFound = StartupGate.FindSolutionAbove(AppContext.BaseDirectory, out _);
        bool powerShell51Found = File.Exists(PowerShell51.ExecutablePath());

        using Mutex instanceMutex = StartupGate.TryAcquireSingleInstance(out bool acquiredInstance);

        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: IsRunningElevated(),
            safeModeVariableSet: safeModeSet,
            dataRootVariableSet: dataRootSet,
            sandboxRequested: sandboxRequested,
            anotherInstanceRunning: !acquiredInstance,
            solutionFound: solutionFound,
            powerShell51Found: powerShell51Found);

        if (refusal != StartupRefusal.None)
        {
            MessageBox.Show(StartupGate.Message(refusal), "Earshot live tests", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            return 1;
        }

        // The mutex stays owned by this process for as long as the window runs, so a second
        // instance's own attempt to create it sees createdNew false the whole time. It is
        // released implicitly when the handle closes at process exit.
        Application.Run(new MainForm());
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
