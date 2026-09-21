namespace Earshot.TestWindow.Core;

// The five things that make the window show a blocking screen and offer nothing else. Kept as a
// pure decision over facts a caller has already collected, so every branch can be driven from a
// test without an elevated process, a real second instance or a machine missing Windows
// PowerShell 5.1.
internal enum StartupRefusal
{
    None = 0,
    RunningElevated,
    SandboxRequestedWithoutFolder,
    SandboxEnvironmentVariableSet,
    AnotherInstanceRunning,
    SolutionNotFound,
    WindowsPowerShell51Missing,
}

internal static class StartupGate
{
    internal const string MutexName = "Local\\EarshotLiveTestWindow";
    internal const string SandboxArgument = "--sandbox";

    // An elevated window is refused before anything else is even worth reading, and a missing
    // PowerShell 5.1 is checked last because every other refusal is cheaper to explain.
    // sandboxRequested must mean a sandbox that was actually usable, not merely the bare
    // presence of the --sandbox switch. Before sandboxArgumentWithoutValidFolder existed, the
    // switch's mere presence (StartupGate.HasSandboxArgument) was used to decide sandboxRequested
    // and, separately and independently, SandboxOptions.TryParse decided whether MainForm actually
    // got a sandbox: --sandbox with nothing after it (or Program.cs's own TryParse call
    // discarding its own success flag) bypassed the environment-variable refusal below while
    // still starting a REAL, non-sandboxed run.
    internal static StartupRefusal Evaluate(
        bool isElevated,
        bool safeModeVariableSet,
        bool dataRootVariableSet,
        bool sandboxRequested,
        bool sandboxArgumentWithoutValidFolder,
        bool anotherInstanceRunning,
        bool solutionFound,
        bool powerShell51Found)
    {
        if (isElevated)
        {
            return StartupRefusal.RunningElevated;
        }

        if (sandboxArgumentWithoutValidFolder)
        {
            return StartupRefusal.SandboxRequestedWithoutFolder;
        }

        if (!sandboxRequested && (safeModeVariableSet || dataRootVariableSet))
        {
            return StartupRefusal.SandboxEnvironmentVariableSet;
        }

        if (anotherInstanceRunning)
        {
            return StartupRefusal.AnotherInstanceRunning;
        }

        if (!solutionFound)
        {
            return StartupRefusal.SolutionNotFound;
        }

        if (!powerShell51Found)
        {
            return StartupRefusal.WindowsPowerShell51Missing;
        }

        return StartupRefusal.None;
    }

    internal static bool HasSandboxArgument(IReadOnlyList<string> args)
    {
        foreach (string argument in args)
        {
            if (string.Equals(argument, SandboxArgument, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The folder after --sandbox, or null when the switch is absent, has nothing after it, or
    // what follows it is empty or all whitespace: none of those is a valid folder either.
    internal static string? SandboxFolder(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], SandboxArgument, StringComparison.Ordinal))
            {
                string candidate = args[i + 1];
                return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
            }
        }

        return null;
    }

    // The scripts are found from the repository: walk up from the exe looking for Earshot.slnx,
    // the same way RepositoryRoot() does in the test project.
    internal static bool FindSolutionAbove(string startDirectory, out string? solutionRoot)
    {
        for (DirectoryInfo? folder = new(startDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Earshot.slnx")))
            {
                solutionRoot = folder.FullName;
                return true;
            }
        }

        solutionRoot = null;
        return false;
    }

    // Tries to become the one running copy. The mutex is created either way; the caller finds out
    // whether it already existed and must dispose what it is handed exactly once.
    internal static Mutex TryAcquireSingleInstance(out bool acquired) => TryAcquireSingleInstance(MutexName, out acquired);

    // The name is a parameter so a test can prove the mechanism under a name of its own, rather
    // than fighting a real window's mutex on the machine running the test.
    internal static Mutex TryAcquireSingleInstance(string mutexName, out bool acquired)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        acquired = createdNew;
        return mutex;
    }

    internal static string Message(StartupRefusal refusal) => refusal switch
    {
        StartupRefusal.RunningElevated =>
            "This window is running as administrator. Tests 06 and 07 need a normal window. Close it and open it normally.",
        StartupRefusal.SandboxRequestedWithoutFolder =>
            "--sandbox needs a folder after it, for example --sandbox C:\\temp\\sandbox. Nothing was started.",
        StartupRefusal.SandboxEnvironmentVariableSet =>
            "EARSHOT_SAFE_MODE or EARSHOT_DATA_ROOT is set for this window. A real run never has either set. " +
            "Close the window that set it, or start this one with --sandbox for development.",
        StartupRefusal.AnotherInstanceRunning =>
            "Earshot live tests is already open. Only one copy runs at a time.",
        StartupRefusal.SolutionNotFound =>
            "Earshot.slnx was not found above this program. The test window only runs from inside the repository.",
        StartupRefusal.WindowsPowerShell51Missing =>
            "Windows PowerShell 5.1 was not found at " + PowerShell51.ExecutablePath() + ". The live tests need it.",
        _ => string.Empty,
    };
}
