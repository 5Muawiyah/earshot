using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// An administrator prompt raises only %ProgramFiles%\Earshot\Earshot.exe. A copy run from Downloads or a build folder sits where
// a standard user can change it, so with no installed copy the trace and Fix connection's apply step say to set up Earshot
// first instead of raising the running copy.
[TestClass]
public sealed class InstalledCopyOnlyTests
{
    private static Paths Real() => Paths.FromEnvironment(_ => null);

    [TestMethod]
    public void TheInstalledCopyIsNamedWhenItIsThereAndNothingOtherwise()
    {
        Paths paths = Real();
        bool present = false;
        Func<string?> installed = Program.InstalledCopy(paths, path => present && path == paths.InstalledExe);

        Assert.IsNull(installed(), "No installed copy: nothing to raise, and never the running copy.");
        present = true;
        Assert.AreEqual(paths.InstalledExe, installed(), "Read each time, so a set-up while the tray runs is seen.");
    }

    [TestMethod]
    public void TheTraysRecorderNeedsSetUpExactlyWhenThereIsNoInstalledCopy()
    {
        Paths paths = Real();
        using Earshot.Tracing.ITraceRecorder? recorder = Program.CreateTraceRecorder(paths, new CapturingLog());

        Assert.IsNotNull(recorder);
        Assert.AreEqual(!File.Exists(paths.InstalledExe), recorder.NeedsSetUp);
    }
}
