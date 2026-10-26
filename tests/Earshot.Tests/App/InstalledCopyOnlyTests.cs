using Earshot.App;
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
        Func<string?> installed = Program.InstalledCopy(paths, path => present && path == paths.InstalledExe, _ => true, "1.6.0", _ => "1.6.0");

        Assert.IsNull(installed(), "No installed copy: nothing to raise, and never the running copy.");
        present = true;
        Assert.AreEqual(paths.InstalledExe, installed(), "Read each time, so a set-up while the tray runs is seen.");
    }

    // An older installed program has no such verb and would take it as a request to start its tray, elevated.
    [TestMethod]
    [DataRow("1.5.0", false)]
    [DataRow("", false)]
    [DataRow("1.6.0", true)]
    [DataRow("1.10.0", true)]
    public void AnInstalledCopyOlderThanTheVerbIsNotRaised(string version, bool raised)
    {
        Paths paths = Real();

        string? chosen = Program.InstalledCopy(paths, _ => true, _ => true, Program.TraceVerbSince, _ => version)();

        Assert.AreEqual(raised, chosen is not null);
    }

    // An installed copy whose folder a standard user can change is not raised: the same folder check the update hand-over
    // makes. Before, the program being there and new enough was all that was asked.
    [TestMethod]
    public void AnInstalledCopyInAFolderThatFailsItsCheckIsNotRaised()
    {
        Paths paths = Real();

        string? chosen = Program.InstalledCopy(paths, _ => true, _ => false, Program.TraceVerbSince, _ => "1.6.0")();

        Assert.IsNull(chosen);
    }

    // The real check, against a folder Users can write to.
    [TestMethod]
    public void TheRealFolderCheckRefusesAFolderUsersCanWrite()
    {
        using var folder = new TempFolder();
        string exe = folder.File("Earshot.exe");
        File.WriteAllText(exe, "x");
        var log = new CapturingLog();

        Assert.IsFalse(Program.InstallFolderTrusted(log)(exe));
        Assert.IsTrue(log.Entries.Any(e => e.Message.StartsWith("The installed copy is not raised: ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheTraysRecorderNeedsSetUpExactlyWhenThereIsNoInstalledCopy()
    {
        Paths paths = Real();
        using Earshot.Tracing.ITraceRecorder? recorder = Program.CreateTraceRecorder(paths, new CapturingLog());

        Assert.IsNotNull(recorder);
        Assert.AreEqual(Program.InstalledCopy(paths, File.Exists, Program.InstallFolderTrusted(new CapturingLog()), Program.TraceVerbSince, SetupValues.ProgramVersion)() is null, recorder.NeedsSetUp);
        Assert.AreEqual(paths.UserTraceFolder, recorder.DestinationFolder);
    }
}
