namespace Earshot.TestWindow.Core;

// The five things that make the window show a blocking screen and offer nothing else
// (design.md section 8.1). Kept as a pure decision over facts a caller has already collected, so
// every branch can be driven from a test without an elevated process, a real second instance or a
// machine missing Windows PowerShell 5.1.
internal enum StartupRefusal
{
    None = 0,
    RunningElevated,
    SandboxEnvironmentVariableSet,
    AnotherInstanceRunning,
    SolutionNotFound,
    WindowsPowerShell51Missing,
}

internal static class StartupGate
{
    internal const string MutexName = "Local\\EarshotLiveTestWindow";
    internal const string SandboxArgument = "--sandbox";

    // Order matches the bullet list in design.md 8.1. An elevated window is refused before
    // anything else is even worth reading, and a missing PowerShell 5.1 is checked last because
    // every other refusal is cheaper to explain.
    internal static StartupRefusal Evaluate(
        bool isElevated,
        bool safeModeVariableSet,
        bool dataRootVariableSet,
        bool sandboxRequested,
        bool anotherInstanceRunning,
        bool solutionFound,
        bool powerShell51Found)
    {
        if (isElevated)
        {
            return StartupRefusal.RunningElevated;
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

    // The folder after --sandbox, or null when the switch is absent or has nothing after it.
    internal static string? SandboxFolder(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], SandboxArgument, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    // The scripts are found from the repository (design.md 8.1): walk up from the exe looking for
    // Earshot.slnx, the same way RepositoryRoot() does in the test project.
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
