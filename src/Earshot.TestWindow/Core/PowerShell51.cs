using System.Diagnostics;

namespace Earshot.TestWindow.Core;

// Where Windows PowerShell 5.1 lives, and how a child of it is started so it never inherits a
// PowerShell 7 parent's PSModulePath. This mirrors
// tests\Earshot.Tests\LiveTests\WindowsPowerShellHost.cs; that type is internal to the test
// assembly and this window has no reference to it, so the same small piece of behaviour is kept
// here rather than shared across the boundary the build must not cross.
// https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_psmodulepath
internal static class PowerShell51
{
    internal const string ModulePathVariable = "PSModulePath";

    // Where Windows PowerShell 5.1 lives on every Windows install. Returned whether or not it is
    // there: the caller decides what an absent host means.
    internal static string ExecutablePath()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    // With PSModulePath removed, the child builds its own default path, which is what the
    // owner's own console gives the shipped scripts.
    internal static ProcessStartInfo CreateStartInfo(string host, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ModulePathVariable);
        return info;
    }
}
