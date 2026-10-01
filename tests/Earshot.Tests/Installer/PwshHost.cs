using System.Diagnostics;
using Earshot.Tests.LiveTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// The two PowerShells the installer script runs in: Windows PowerShell 5.1, which every Windows PC has, and PowerShell 7
// (pwsh.exe), which this PC may not have. A test row for 7 is a skip where it is absent, and the hosted build, whose image
// has it, runs the row and must not skip it: EARSHOT_REQUIRE_PWSH=1 turns the skip into a failure there, so a hosted run can
// never pass by quietly running only half.
public enum ShellKind
{
    WindowsPowerShell,
    PowerShell7,
}

internal static class PwshHost
{
    internal const string RequireVariable = "EARSHOT_REQUIRE_PWSH";

    // pwsh.exe on PATH, then where its installer puts it. Null when there is none.
    internal static string? Find()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim('"'), "pwsh.exe");
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(installed) ? installed : null;
    }

    // The program to run for a shell. Windows PowerShell 5.1 is required to be there. PowerShell 7 is Inconclusive when it is
    // absent, or a failure where the build says it must be there.
    internal static string Require(ShellKind shell)
    {
        if (shell == ShellKind.WindowsPowerShell)
        {
            string host = WindowsPowerShellHost.Path51();
            Assert.IsTrue(File.Exists(host), "Windows PowerShell 5.1 is missing: " + host);
            return host;
        }

        string? pwsh = Find();
        if (pwsh is not null)
        {
            return pwsh;
        }

        if (Environment.GetEnvironmentVariable(RequireVariable) == "1")
        {
            Assert.Fail("pwsh.exe was not found, and " + RequireVariable + "=1 says this build must run the PowerShell 7 rows.");
        }

        Assert.Inconclusive("pwsh.exe is not on this PC, so the PowerShell 7 row did not run here. The hosted build runs it.");
        return "";
    }

    internal static ProcessStartInfo CreateStartInfo(ShellKind shell, string host, IEnumerable<string> arguments) =>
        shell == ShellKind.WindowsPowerShell
            ? WindowsPowerShellHost.CreateStartInfo(host, arguments)
            : PlainStartInfo(host, arguments);

    private static ProcessStartInfo PlainStartInfo(string host, IEnumerable<string> arguments)
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

        return info;
    }
}
