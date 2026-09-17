using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Only refusals and exit-code resolution are exercised. No test dispatches a mode that is allowed to
// run, because once its hook exists it would perform the real action (or start the tray).
[TestClass]
public sealed class ProgramDispatchTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static readonly string[] ExpectedPrivilegedModes = ["gate", "gate-protect", "install", "uninstall"];

    private static Paths PathsWith(string? dataRoot, string? safeMode) =>
        Paths.FromEnvironment(name => name switch
        {
            Paths.DataRootVariable => dataRoot,
            Paths.SafeModeVariable => safeMode,
            _ => null,
        });

    [TestMethod]
    [DataRow("gate", "block", Nonce)]
    [DataRow("gate", "boot")]
    [DataRow("gate-protect", "protect-on", Nonce)]
    [DataRow("install", "S-1-5-21-1-2-3-1001", "5A6B7C8D9EAF", "1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D")]
    [DataRow("uninstall")]
    public void SafeModeRefusesPrivilegedModesBeforeDispatch(params string[] args)
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();

        int exit = Program.Dispatch(args, PathsWith(temp.Path, "1"), log);

        Assert.AreEqual(ExitCodes.Refused, exit);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode: no device actions. Refused: " + args[0] + "."));
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path), "A refused mode writes nothing under the data root.");
    }

    [TestMethod]
    [DataRow("gate", "block", Nonce)]
    [DataRow("gate-protect", "protect-off", Nonce)]
    [DataRow("install", "S-1-5-21-1-2-3-1001", "5A6B7C8D9EAF", "1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D")]
    [DataRow("uninstall")]
    public void ADataRootRefusesPrivilegedModesEvenOutsideSafeMode(params string[] args)
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();

        int exit = Program.Dispatch(args, PathsWith(temp.Path, safeMode: null), log);

        Assert.AreEqual(ExitCodes.Refused, exit);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Refused: " + args[0] + " does not run while EARSHOT_DATA_ROOT is set."));
    }

    [TestMethod]
    public void PrivilegedModesRunOnlyWithTheRealPathsAndNoSafeMode()
    {
        using var temp = new TempFolder();

        foreach (string mode in Program.PrivilegedModes)
        {
            Assert.IsNull(Program.PrivilegedModeRefusal(mode, PathsWith(dataRoot: null, safeMode: null)), mode);
            Assert.IsNotNull(Program.PrivilegedModeRefusal(mode, PathsWith(dataRoot: null, safeMode: "1")), mode);
            Assert.IsNotNull(Program.PrivilegedModeRefusal(mode, PathsWith(temp.Path, safeMode: null)), mode);
            Assert.IsNotNull(Program.PrivilegedModeRefusal(mode, PathsWith(temp.Path, safeMode: "1")), mode);
        }

        CollectionAssert.AreEquivalent(ExpectedPrivilegedModes, Program.PrivilegedModes.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--startup")]
    [DataRow("probe")]
    [DataRow("diag")]
    [DataRow("Gate")]
    [DataRow("GATE-PROTECT")]
    [DataRow("INSTALL")]
    public void OtherModesAreNotRefusedHere(string mode)
    {
        using var temp = new TempFolder();

        Assert.IsNull(Program.PrivilegedModeRefusal(mode, PathsWith(temp.Path, "1")));
    }

    [TestMethod]
    [DataRow("tray")]
    [DataRow("probe")]
    [DataRow("gate")]
    public void AModeWhoseHookDidNotRunIsUnavailableAndLogged(string label)
    {
        var log = new CapturingLog();
        var ctx = new Program.RunContext { Args = [] };

        Assert.AreEqual(ExitCodes.Unavailable, Program.ExitCodeFor(label, ctx, log));
        Assert.IsTrue(log.Has(LogLevel.Error, "'" + label + "': Not available in this build."));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(77)]
    public void AnExitCodeSetByTheHookIsReturnedAsItIs(int code)
    {
        var log = new CapturingLog();
        var ctx = new Program.RunContext { Args = [], ExitCode = code };

        Assert.AreEqual(code, Program.ExitCodeFor("tray", ctx, log));
        Assert.IsEmpty(log.Entries);
    }
}
