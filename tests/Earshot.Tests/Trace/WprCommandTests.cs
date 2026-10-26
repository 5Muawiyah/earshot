using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// The forms proved on this PC on 25 October: -start "<file>!BluetoothStack.Verbose" -filemode starts a recording; the
// ".File" id form is refused with 0xc5600611. -recordtempto keeps the file being recorded in the private run folder, not in the
// user's %TEMP%, and -instancename, last on every line, keeps -stop and -cancel to Earshot's own session.
// https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options
[TestClass]
public sealed class WprCommandTests
{
    private static readonly string[] ExpectedStart =
    [
        "-start", @"C:\ProgramData\Earshot\trace\x\BluetoothStack.wprp!BluetoothStack.Verbose", "-filemode",
        "-recordtempto", @"C:\ProgramData\Earshot\trace\x", "-instancename", WprCommand.InstanceName,
    ];

    private static readonly string[] ExpectedStop = ["-stop", @"C:\t\a.etl", "-instancename", WprCommand.InstanceName];
    private static readonly string[] ExpectedCancel = ["-cancel", "-instancename", WprCommand.InstanceName];
    private static readonly string[] ExpectedHelp = ["-help"];

    [TestMethod]
    public void StartNamesTheVerboseProfileWithoutTheFileSuffixAndRecordsToAFile()
    {
        IReadOnlyList<string> args = WprCommand.Start(@"C:\ProgramData\Earshot\trace\x\BluetoothStack.wprp", @"C:\ProgramData\Earshot\trace\x");

        CollectionAssert.AreEqual(ExpectedStart, args.ToArray());
        Assert.IsFalse(args[1].EndsWith(".File", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StopSavesToTheGivenFile()
    {
        CollectionAssert.AreEqual(ExpectedStop, WprCommand.Stop(@"C:\t\a.etl").ToArray());
    }

    [TestMethod]
    public void CancelNamesEarshotsOwnSessionAndHelpIsOneWord()
    {
        CollectionAssert.AreEqual(ExpectedCancel, WprCommand.Cancel.ToArray());
        CollectionAssert.AreEqual(ExpectedHelp, WprCommand.Help.ToArray());
    }

    [TestMethod]
    public void TheInstanceNameIsTheLastArgumentOfEveryLineThatTouchesTheSession()
    {
        foreach (IReadOnlyList<string> line in new[] { WprCommand.Start(@"C:\p.wprp", @"C:\t"), WprCommand.Stop(@"C:\t\a.etl"), WprCommand.Cancel })
        {
            Assert.AreEqual("-instancename", line[^2], string.Join(' ', line));
            Assert.AreEqual(WprCommand.InstanceName, line[^1]);
        }
    }

    // Another signed-in user's Earshot records under its own session's name, so this session's cancel cannot end it.
    [TestMethod]
    public void TheInstanceNameCarriesThisSession()
    {
        Assert.AreEqual("EarshotBluetoothTraceS1", WprCommand.InstanceNameFor(1));
        Assert.AreEqual("EarshotBluetoothTraceS2", WprCommand.InstanceNameFor(2));
        Assert.AreEqual(WprCommand.InstanceNameFor(System.Diagnostics.Process.GetCurrentProcess().SessionId), WprCommand.InstanceName, "The name in use is this process's session's.");
    }

    [TestMethod]
    public void TheRecordingIsKeptInTheFolderTheCallerGivesNotInTemp()
    {
        IReadOnlyList<string> args = WprCommand.Start(@"C:\p.wprp", @"C:\ProgramData\Earshot\trace\n");

        int at = args.ToList().IndexOf("-recordtempto");
        Assert.IsTrue(at > 0);
        Assert.AreEqual(@"C:\ProgramData\Earshot\trace\n", args[at + 1]);
        Assert.ThrowsExactly<ArgumentException>(() => WprCommand.Start(@"C:\p.wprp", @"relative"));
    }

    [TestMethod]
    public void TheProgramIsTheOneInSystem32()
    {
        Assert.AreEqual(@"C:\Windows\System32\wpr.exe", WprCommand.ExePath(@"C:\Windows"));
        Assert.AreEqual(@"C:\Windows\System32\wpr.exe", WprCommand.ExePath(@"C:\Windows\"));
    }

    [TestMethod]
    public void ARelativeSystemRootIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WprCommand.ExePath(@"Windows"));
    }
}
