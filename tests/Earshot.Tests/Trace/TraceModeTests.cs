using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// The trace verb: its command line, its refusals, and what it is allowed to reach.
[TestClass]
public sealed class TraceModeTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void AGoodCommandLineParses()
    {
        Assert.IsTrue(TraceRequest.TryParse(["trace", "4321", Nonce], out TraceRequest? request, out _));
        Assert.AreEqual(4321, request!.TrayPid);
        Assert.AreEqual(Nonce, request.Nonce);
    }

    [TestMethod]
    [DataRow("trace")]
    [DataRow("trace", "4321")]
    [DataRow("trace", "0", "0123456789abcdef0123456789abcdef")]
    [DataRow("trace", "-5", "0123456789abcdef0123456789abcdef")]
    [DataRow("trace", "abc", "0123456789abcdef0123456789abcdef")]
    [DataRow("trace", "4321", "NOT-A-NONCE")]
    [DataRow("trace", "4321", "0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("trace", "4321", "0123456789abcdef0123456789abcdef", "extra")]
    public void AnythingElseIsRejected(params string[] args)
    {
        Assert.IsFalse(TraceRequest.TryParse(args, out _, out string? problem));
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void TraceIsAPrivilegedModeSoSafeModeAndARedirectedRootRefuseIt()
    {
        Assert.IsTrue(Program.PrivilegedModes.Contains("trace"));

        Paths safe = Paths.FromEnvironment(n => n == Paths.SafeModeVariable ? "1" : null);
        Paths redirected = Paths.FromEnvironment(n => n == Paths.DataRootVariable ? Path.GetTempPath() : null);

        Assert.IsNotNull(Program.PrivilegedModeRefusal("trace", safe));
        Assert.IsNotNull(Program.PrivilegedModeRefusal("trace", redirected));
        Assert.IsNull(Program.PrivilegedModeRefusal("trace", Paths.FromEnvironment(_ => null)));
    }

    [TestMethod]
    public void TheDispatcherRoutesTraceToItsOwnModeNotTheTray()
    {
        // In safe mode the refusal happens before any mode runs, which is the cheapest real execution of the dispatch.
        Paths safe = Paths.FromEnvironment(n => n == Paths.SafeModeVariable ? "1" : null);

        int code = Program.Dispatch(["trace", "1", Nonce], safe, new CapturingLog());

        Assert.AreEqual(ExitCodes.Refused, code);
    }

    [TestMethod]
    public void TheModeRefusesSafeModeAndARedirectedRootItself()
    {
        Assert.IsNotNull(Program.TracePathsRefusal(Paths.FromEnvironment(n => n == Paths.SafeModeVariable ? "1" : null)));
        Assert.IsNotNull(Program.TracePathsRefusal(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? Path.GetTempPath() : null)));
        Assert.IsNull(Program.TracePathsRefusal(Paths.FromEnvironment(_ => null)));
    }

    [TestMethod]
    public void TheModeRunsTheHelperWithTheRequestAndReturnsItsExitCode()
    {
        TraceRequest? seen = null;
        int code = Program.RunTrace(["trace", "4321", Nonce], new CapturingLog(), request =>
        {
            seen = request;
            return TraceOutcome.WprMissing;
        });

        Assert.AreEqual(ExitCodes.Unavailable, code);
        Assert.AreEqual(4321, seen!.TrayPid);
    }

    [TestMethod]
    public void ABadCommandLineRunsNothingAndIsUsage()
    {
        bool ran = false;
        int code = Program.RunTrace(["trace", "nope"], new CapturingLog(), _ =>
        {
            ran = true;
            return TraceOutcome.Saved;
        });

        Assert.AreEqual(ExitCodes.Usage, code);
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public void TheMarkerRoundTripsAndRejectsJunk()
    {
        string text = TraceMarker.Serialize(new TraceMarker(5150, Nonce));
        Assert.IsTrue(TraceMarker.TryParse(text, out TraceMarker? marker));
        Assert.AreEqual(5150, marker!.HelperPid);
        Assert.AreEqual(Nonce, marker.Nonce);
        Assert.IsFalse(TraceMarker.TryParse("helper=abc\nnonce=x", out _));
        Assert.IsFalse(TraceMarker.TryParse("", out _));
    }

    [TestMethod]
    public void TheResultFileRoundTripsAndRejectsJunk()
    {
        string text = TraceResultFile.Serialize(new TraceResultFile(TraceOutcome.StartFailed, "", unchecked((int)0xc5600611)));
        Assert.IsTrue(TraceResultFile.TryParse(text, out TraceResultFile? parsed));
        Assert.AreEqual(TraceOutcome.StartFailed, parsed!.Outcome);
        Assert.AreEqual(unchecked((int)0xc5600611), parsed.WprCode);
        Assert.IsFalse(TraceResultFile.TryParse("outcome=Bogus", out _));
        Assert.IsFalse(TraceResultFile.TryParse("", out _));
    }

    [TestMethod]
    public void AFileNameInAResultThatLeavesItsFolderIsRejected()
    {
        Assert.IsFalse(TraceResultFile.TryParse("outcome=Saved\nfile=..\\..\\Windows\\x.etl\nwpr=0", out _));
        Assert.IsFalse(TraceResultFile.TryParse("outcome=Saved\nfile=C:\\x.etl\nwpr=0", out _));
    }
}
