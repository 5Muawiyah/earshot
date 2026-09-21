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

    // The four real folders a sandbox folder must never be inside: exactly what
    // SandboxOptions.ChildEnvironment redirects LOCALAPPDATA, APPDATA, ProgramData and
    // ProgramFiles away from for the sandboxed child. A --sandbox folder somewhere underneath one
    // of these would leave those redirected paths still inside the real one, so the child's
    // "sandboxed" writes would land on this machine's real data after all.
    internal static IReadOnlyList<string> RealProtectedRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("ProgramData") ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetEnvironmentVariable("ProgramFiles") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    };

    // The folder after --sandbox, or null when the switch is absent, has nothing after it, or
    // what follows it is not a usable sandbox folder. Before this, any non-whitespace text
    // counted: a following switch ("--sandbox --other") read as a literal folder named
    // "--other", a relative path resolved against whatever the process's own current directory
    // happened to be, and a folder that was itself inside the real %LOCALAPPDATA% (or the other
    // three redirected roots) left the "sandboxed" child writing to this machine's real data
    // after all, none of which SandboxRequestedWithoutFolder's own refusal ever caught.
    internal static string? SandboxFolder(IReadOnlyList<string> args) => SandboxFolder(args, RealProtectedRoots());

    // protectedRoots is a parameter, not RealProtectedRoots() read directly, so a test can supply
    // folders of its own rather than fighting whatever this machine's real %LOCALAPPDATA% happens
    // to be.
    internal static string? SandboxFolder(IReadOnlyList<string> args, IReadOnlyList<string> protectedRoots)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], SandboxArgument, StringComparison.Ordinal))
            {
                string candidate = args[i + 1];
                return IsUsableSandboxFolder(candidate, protectedRoots) ? candidate : null;
            }
        }

        return null;
    }

    // A usable --sandbox folder: an absolute path (never relative, and never another switch,
    // which a missing folder argument would otherwise read as one, since it is just as much
    // non-whitespace text as a real path), naming a folder that actually exists and holds either
    // nothing at all or only this same sandbox's own four redirected sub-folders (safe to reuse
    // across runs), outside every one of the real roots those four sub-folders stand in for.
    private static bool IsUsableSandboxFolder(string candidate, IReadOnlyList<string> protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        string full = Path.GetFullPath(candidate);
        foreach (string root in protectedRoots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            string fullRoot = Path.GetFullPath(root);
            if (string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return Directory.Exists(full) && IsEmptyOrAnEarlierSandboxLayout(full);
    }

    private static readonly string[] SandboxSubFolderNames = { "local", "roaming", "programdata", "programfiles" };

    private static bool IsEmptyOrAnEarlierSandboxLayout(string folder)
    {
        string[] entries = Directory.GetFileSystemEntries(folder);
        return entries.All(entry => SandboxSubFolderNames.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase));
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
