namespace Earshot.TestWindow.Core;

// M4: everything about starting the administrator prompt check that does not itself start a
// process, so a test can check the paths a real launch would use without ever calling
// ChildRunner.Start on them (which would raise a real Windows administrator prompt). Only
// MainForm.StartRehearsal actually starts anything, and only when it is not a sandbox window.
internal static class RehearsalLaunch
{
    // Never the sandbox driver: section 8.1's own sandbox redirection only ever stands in for the
    // device, and this check's whole point is that nothing about it can be faked (D8: "the site
    // has never been executed in any form"). test-gui.md 10.2 names Test-ElevatedLaunch.ps1 as
    // "started through the same driver, shim and child settings as a real half."
    internal static string ScriptPath(string repoRoot) =>
        Path.Combine(repoRoot, "tools", "live-tests", "gui", "Test-ElevatedLaunch.ps1");

    internal static string DriverPath(string repoRoot) =>
        Path.Combine(repoRoot, "tools", "live-tests", "gui", "Invoke-GuiHalf.ps1");
}
