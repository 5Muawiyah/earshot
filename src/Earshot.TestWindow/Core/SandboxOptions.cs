namespace Earshot.TestWindow.Core;

// design.md section 8.1: "--sandbox <folder> (tests and development only: uses
// Run-GuiHalfAgainstFakes.ps1, redirects LOCALAPPDATA, APPDATA, ProgramData, ProgramFiles for
// the child into that folder, and titles the window 'SANDBOX, no device')."
internal sealed class SandboxOptions
{
    internal const string DriverScriptName = "Run-GuiHalfAgainstFakes.ps1";
    internal const string WindowTitleSuffix = "SANDBOX, no device";

    internal required string Folder { get; init; }

    // The four variables New-LiveTestRun and Resolve-EarshotExe read to find the machine's own
    // folders. Redirecting them is what keeps a sandboxed run off this machine's real
    // %LOCALAPPDATA%\Earshot, even though the device side is stubbed anyway. The folder names
    // (local, roaming, programdata, programfiles) match
    // tools\live-tests\selftest\Invoke-SelfTest.ps1's own redirection exactly, because
    // tools\live-tests\selftest\Fakes.psm1 (unchanged) computes its own fake paths
    // (New-FakeSandbox, Initialize-FakeMachine) against that same layout.
    //
    // EARSHOT_SAFE_MODE and EARSHOT_DATA_ROOT are cleared here, and only here: the window's own
    // process may need them set (a caller who sets them belt-and-braces alongside --sandbox), but
    // the fakes driver's child still calls the real, unstubbed New-LiveTestRun, whose
    // Assert-LiveEnvironment refuses to run at all while either is set, the same reason
    // selftest\Invoke-SelfTest.ps1 clears them around its own child. design.md section 8.1's "the
    // variables are never stripped" is about the real (non-sandbox) driver, where
    // Assert-LiveEnvironment must stay the only authority.
    internal IReadOnlyDictionary<string, string> ChildEnvironment => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LOCALAPPDATA"] = Path.Combine(Folder, "local"),
        ["APPDATA"] = Path.Combine(Folder, "roaming"),
        ["ProgramData"] = Path.Combine(Folder, "programdata"),
        ["ProgramFiles"] = Path.Combine(Folder, "programfiles"),
        ["EARSHOT_SAFE_MODE"] = string.Empty,
        ["EARSHOT_DATA_ROOT"] = string.Empty,
    };

    internal static bool TryParse(IReadOnlyList<string> args, out SandboxOptions? options)
    {
        string? folder = StartupGate.SandboxFolder(args);
        if (folder is null)
        {
            options = null;
            return false;
        }

        options = new SandboxOptions { Folder = folder };
        return true;
    }
}
