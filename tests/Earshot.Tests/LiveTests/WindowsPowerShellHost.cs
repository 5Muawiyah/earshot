using System.Diagnostics;

namespace Earshot.Tests.LiveTests;

// How the tests start Windows PowerShell 5.1, because the environment the child inherits decides
// whether it works.
//
// The hosted build runs the gate under PowerShell 7, and PowerShell 7 puts its own module folders
// at the front of PSModulePath. It takes them out again when it starts powershell.exe itself, but
// not when something in between does: here the chain is pwsh, dotnet test, then powershell.exe.
// A 5.1 child that inherited the 7 path stopped in Invoke-SelfTest.ps1 on
// "'Import-PowerShellDataFile' is not recognized", CommandNotFoundException, before printing
// anything, while the compiled cmdlets around it still resolved. 5.1 ships that command as a
// script function inside Microsoft.PowerShell.Utility and 7 lists it as a cmdlet, so the likely
// mechanism is that 5.1 resolved 7's copy of the module, which does not give it the function.
// That part is inferred: PowerShell 7 is not installed where this was written. What is proved,
// by the hosted build going from failing to passing on this change alone, is that the inherited
// path was the cause. Every run of the build workflow had failed on that one test while the
// same gate passed on a machine that runs it under 5.1.
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
