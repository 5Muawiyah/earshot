using System.Diagnostics;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The product-side equivalent of tests\Earshot.Tests\LiveTests\WindowsPowerShellHost: every place
// the window (through ChildRunner, slice S3) or its driver starts powershell.exe 5.1 must not
// hand the child a PowerShell 7 parent's PSModulePath, the same fact WindowsPowerShellHostTests
// pins for the test side. This is the one execution proving PowerShell51.CreateStartInfo does it.
[TestClass]
public sealed class PowerShell51Tests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] PrintOkArguments = { "-NoProfile", "-NonInteractive", "-Command", "'ok'" };

    [TestMethod]
    public void CreateStartInfoNeverCarriesPSModulePathIntoTheChildsEnvironment()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        ProcessStartInfo info = PowerShell51.CreateStartInfo(host, PrintOkArguments);

        Assert.IsFalse(
            info.Environment.ContainsKey(PowerShell51.ModulePathVariable),
            "A child started through PowerShell51.CreateStartInfo would inherit " + PowerShell51.ModulePathVariable + ".");
    }

    // A real child, not only the ProcessStartInfo it was never asked to run.
    //
    // A local probe on this machine disagrees with the assumption that removing the variable
    // before Process.Start leaves it unset inside the child: Windows PowerShell 5.1 itself
    // populates $env:PSModulePath at start-up when it finds none inherited (about_PSModulePath:
    // "if the PSModulePath environment variable does not exist, Windows PowerShell creates it").
    // So the variable reads "present" here, with a value 5.1 computed itself, never a 7 parent's
    // value; that is the fact worth proving, and it is what actually decides whether
    // Import-PowerShellDataFile resolves, the same probe WindowsPowerShellHostTests runs for the
    // test side.
    [TestMethod]
    public void ARealChildCanResolveImportPowerShellDataFile()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string folder = Path.Combine(Path.GetTempPath(), "earshot-ps51-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string dataPath = Path.Combine(folder, "data.psd1");
            string scriptPath = Path.Combine(folder, "probe.ps1");
            File.WriteAllText(dataPath, "@{ Answer = 42 }" + Environment.NewLine);
            File.WriteAllText(scriptPath,
                "param([string]$Data)" + Environment.NewLine +
                "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
                "(Import-PowerShellDataFile -LiteralPath $Data).Answer" + Environment.NewLine);

            ProcessStartInfo info = PowerShell51.CreateStartInfo(host, new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Data", dataPath,
            });

            using var process = new Process { StartInfo = info };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(RunTimeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The child did not finish within " + RunTimeout + ".");
            }

            Assert.AreEqual(0, process.ExitCode, errors.GetAwaiter().GetResult());
            Assert.AreEqual("42", output.GetAwaiter().GetResult().Trim());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
