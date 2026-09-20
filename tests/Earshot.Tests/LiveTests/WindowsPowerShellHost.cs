using System.Diagnostics;

namespace Earshot.Tests.LiveTests;

// How the tests start Windows PowerShell 5.1, in one place, because the environment the child
// inherits decides whether it works.
//
// The hosted build runs the gate under PowerShell 7, and PowerShell 7 puts its own module folders
// at the front of PSModulePath. It takes them out again when it starts powershell.exe itself, but
// not when something in between does: here the chain is pwsh, dotnet test, then powershell.exe.
// A 5.1 child that inherits the 7 path finds the 7 copy of Microsoft.PowerShell.Utility first and
// cannot load it. The compiled cmdlets still resolve; Import-PowerShellDataFile, which 5.1 ships
// as a script function inside that module, does not, and Invoke-SelfTest.ps1 stopped on it with
// CommandNotFoundException before printing anything. Every run of the build workflow failed on
// that one test while the same gate passed on a machine that runs it under 5.1.
// https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_psmodulepath
//
// With the variable absent, powershell.exe builds its own default path, which is what the owner's
// console gives the shipped scripts.
internal static class WindowsPowerShellHost
{
    internal const string ModulePathVariable = "PSModulePath";

    // Where Windows PowerShell 5.1 lives on every Windows install. Returned whether or not it is
    // there: the caller decides what an absent host means.
    internal static string Path51()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    internal static ProcessStartInfo CreateStartInfo(string host, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ModulePathVariable);
        return info;
    }
}
