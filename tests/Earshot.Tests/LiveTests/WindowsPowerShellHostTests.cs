using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The build workflow failed on every run because a Windows PowerShell 5.1 child inherited a module
// path it could not use (see WindowsPowerShellHost). These two tests hold that shut.
//
// The second is the control. It puts a module in front of the real Microsoft.PowerShell.Utility
// that names Import-PowerShellDataFile as a cmdlet and does not provide it, which is how
// PowerShell 7's manifest looks to 5.1, and shows that 5.1 then answers "is not recognized", the
// text the hosted build printed. So the first test is known to be looking at something that can
// go wrong. The shadowing module is a stand-in written by the test, not PowerShell 7's own: the
// hosted build, which runs the gate under pwsh, is where the real one is exercised.
[TestClass]
public sealed class WindowsPowerShellHostTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void AChildStartedThroughTheHostInheritsNoModulePathAndCanReadADataFile()
    {
        string host = RequireHost();
        string folder = NewScratchFolder();
        try
        {
            ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, ProbeArguments(folder));

            Assert.IsFalse(
                info.Environment.ContainsKey(WindowsPowerShellHost.ModulePathVariable),
                "The child would inherit " + WindowsPowerShellHost.ModulePathVariable + " from whatever started the tests.");

            (int exit, string output, string errors) = Run(info);
            Assert.AreEqual("42", output.Trim(), "Import-PowerShellDataFile did not answer. Exit " + exit + Environment.NewLine + errors);
            Assert.AreEqual(0, exit, errors);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void AModuleAheadOfTheRealOneThatNamesTheCommandWithoutProvidingItTakesItAway()
    {
        string host = RequireHost();
        string folder = NewScratchFolder();
        try
        {
            string shadow = Path.Combine(folder, "shadow");
            string module = Path.Combine(shadow, "Microsoft.PowerShell.Utility");
            Directory.CreateDirectory(module);
            File.WriteAllText(
                Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
                "@{" + Environment.NewLine +
                "    ModuleVersion = '7.0.0.0'" + Environment.NewLine +
                "    GUID = '1da87e53-152b-403e-98dc-74d7b4d63d59'" + Environment.NewLine +
                "    CmdletsToExport = @('Import-PowerShellDataFile')" + Environment.NewLine +
                "    FunctionsToExport = @()" + Environment.NewLine +
                "}" + Environment.NewLine);

            ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, ProbeArguments(folder));
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            info.Environment[WindowsPowerShellHost.ModulePathVariable] =
                shadow + ";" + Path.Combine(system, "WindowsPowerShell", "v1.0", "Modules");

            (int exit, string output, string errors) = Run(info);
            Assert.AreNotEqual("42", output.Trim(), "The shadowing module changed nothing, so the other test proves nothing.");
            Assert.AreNotEqual(0, exit, "The probe exited 0 with the shadowing module in front.");
            StringAssert.Contains(errors, "'Import-PowerShellDataFile' is not recognized");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string RequireHost()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ", so nothing was started.");
        }

        return host;
    }

    private static string NewScratchFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "earshot-ps-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "data.psd1"), "@{ Answer = 42 }" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(folder, "probe.ps1"),
            "param([string]$Data)" + Environment.NewLine +
            "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
            "(Import-PowerShellDataFile -LiteralPath $Data).Answer" + Environment.NewLine);
        return folder;
    }

    private static string[] ProbeArguments(string folder) => new[]
    {
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", Path.Combine(folder, "probe.ps1"), "-Data", Path.Combine(folder, "data.psd1"),
    };

    private static (int Exit, string Output, string Errors) Run(ProcessStartInfo info)
    {
        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Windows PowerShell did not finish within " + RunTimeout + ".");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }
}
