using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Infra;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The fix run is built with the radio check and the connect history, so a Fix started from the tray, from Setup.exe or from the
// install script checks the radio before it removes a pair and writes its one pairing entry. Without them both are quietly off.
[TestClass]
public sealed class FixHostSeamTests
{
    [TestMethod]
    public void TheFixHostCarriesTheRealRadioCheckAndTheConnectHistory()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, temp.Path);
        using var safe = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        Paths paths = Paths.Current;
        var log = new CapturingLog();
        using var cancel = new CancellationTokenSource();
        using var ui = new FixRePairWindow(cancel);
        using var worker = new SystemWorker(log);
        var elevation = new FixElevation(new FixPlanFiles(temp.File("fix")), new ShellRunasFixElevator(new ShellRunasLauncher(), "Earshot.exe"), false, null, null);

        FixHost host = Program.CreateFixHost(paths, log, TextWriter.Null, ui, worker, elevation);

        Assert.IsInstanceOfType<RadioCheck>(host.Radio, "The radio check is the real one.");
        Assert.IsNotNull(host.History, "The pairing entry has a file to go to.");
        Assert.AreEqual(paths.ConnectHistoryFile, host.History.FilePath);
    }

    // Safe mode against the real data folder writes nothing, the connect history included, as the tray's own rule says.
    [TestMethod]
    public void SafeModeAgainstTheRealDataFolderOpensNoHistory()
    {
        Paths real = Paths.FromEnvironment(name => name == Paths.SafeModeVariable ? "1" : null, _ => @"C:\Users\nobody\AppData\Local");
        Assert.IsTrue(real.IsSafeMode);
        Assert.IsFalse(real.IsRedirected);
        Assert.IsNull(Program.OpenConnectHistory(real, new CapturingLog()));

        Paths redirected = Paths.FromEnvironment(
            name => name switch { var n when n == Paths.SafeModeVariable => "1", var n when n == Paths.DataRootVariable => @"C:\Temp\data", _ => null },
            _ => @"C:\Users\nobody\AppData\Local");
        Assert.IsNotNull(Program.OpenConnectHistory(redirected, new CapturingLog()));
    }
}
