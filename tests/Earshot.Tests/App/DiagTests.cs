using System.Globalization;
using Earshot.Composition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Only argument parsing and the safe-mode refusal are tested. No test ever runs a diag target
// outside safe mode, because the targets perform live device actions.
[TestClass]
public sealed class DiagTests
{
    private static readonly string[] ReconnectSrc = ["reconnect", "src"];

    private static ServiceRegistry NoServices() =>
        throw new AssertFailedException("A refused diag target must not build the service registry.");

    [TestMethod]
    [DataRow("diag", "connect")]
    [DataRow("diag", "disconnect")]
    [DataRow("diag", "battery-sweep")]
    [DataRow("diag", "ks", "reconnect", "src")]
    [DataRow("diag", "ks", "disconnect", "all")]
    [DataRow("diag", "ks", "reconnect", "wave")]
    [DataRow("diag", "gate", "block")]
    [DataRow("diag", "gate", "status")]
    [DataRow("diag", "gate", "protect-off")]
    [DataRow("diag", "gate", "set-device", "5A6B7C8D9EAF")]
    [DataRow("diag", "protect-unelevated", "on")]
    [DataRow("diag", "protect-unelevated", "off", "--out", "C:\\temp\\d.txt")]
    public void AcceptsTheDocumentedGrammar(params string[] args)
    {
        Assert.IsTrue(Program.TryParseDiagArgs(args, out Program.DiagRequest? request, out string? error), error);
        Assert.AreEqual(args[1], request.Target);
    }

    [TestMethod]
    public void SplitsTargetArgumentsAndOutPath()
    {
        Assert.IsTrue(Program.TryParseDiagArgs(["diag", "--out", "x.txt", "ks", "reconnect", "src"], out Program.DiagRequest? request, out _));

        Assert.AreEqual("ks", request.Target);
        CollectionAssert.AreEqual(ReconnectSrc, request.Args.ToArray());
        Assert.AreEqual("x.txt", request.OutPath);
    }

    [TestMethod]
    [DataRow("diag")]
    [DataRow("diag", "bogus")]
    [DataRow("diag", "Connect")]
    [DataRow("diag", "connect", "now")]
    [DataRow("diag", "ks")]
    [DataRow("diag", "ks", "reconnect")]
    [DataRow("diag", "ks", "pair", "src")]
    [DataRow("diag", "ks", "reconnect", "phone")]
    [DataRow("diag", "ks", "reconnect", "src", "extra")]
    [DataRow("diag", "gate")]
    [DataRow("diag", "gate", "boot")]
    [DataRow("diag", "gate", "install")]
    [DataRow("diag", "gate", "BLOCK")]
    [DataRow("diag", "gate", "block", "5A6B7C8D9EAF")]
    [DataRow("diag", "gate", "set-device")]
    [DataRow("diag", "gate", "set-device", "5A6b7C8d9Eaf")]
    [DataRow("diag", "gate", "set-device", "$(Arg2)")]
    [DataRow("diag", "gate", "set-device", "5A6B7C8D9EAF;calc")]
    [DataRow("diag", "protect-unelevated")]
    [DataRow("diag", "protect-unelevated", "maybe")]
    [DataRow("diag", "battery-sweep", "--out")]
    public void RejectsEverythingElse(params string[] args)
    {
        Assert.IsFalse(Program.TryParseDiagArgs(args, out Program.DiagRequest? request, out string? error));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    [DataRow("connect")]
    [DataRow("disconnect")]
    [DataRow("ks", "reconnect", "all")]
    [DataRow("gate", "allow")]
    [DataRow("gate", "set-device", "5A6B7C8D9EAF")]
    [DataRow("protect-unelevated", "on")]
    public void SafeModeRefusesLiveTargetsBeforeTheyRun(params string[] targetAndArgs)
    {
        var request = new Program.DiagRequest(targetAndArgs[0], targetAndArgs.Skip(1).ToArray(), OutPath: null);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var log = new CapturingLog();
        using var temp = new TempFolder();

        int exit = Program.RunDiag(request, output, temp.Path, safeMode: true, log, NoServices);

        Assert.AreEqual(ExitCodes.Refused, exit);
        Assert.IsTrue(output.ToString().Contains("Safe mode: no device actions.", StringComparison.Ordinal));
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "Refused: diag " + targetAndArgs[0]));
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path), "No evidence is written for a refused target.");
    }
}
