using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// The forms proved on this PC on 25 October: -start "<file>!BluetoothStack.Verbose" -filemode starts a recording; the
// ".File" id form is refused with 0xc5600611.
[TestClass]
public sealed class WprCommandTests
{
    private static readonly string[] ExpectedStart = ["-start", @"C:\ProgramData\Earshot\trace\x\BluetoothStack.wprp!BluetoothStack.Verbose", "-filemode"];
    private static readonly string[] ExpectedStop = ["-stop", @"C:\t\a.etl"];
    private static readonly string[] ExpectedCancel = ["-cancel"];
    private static readonly string[] ExpectedHelp = ["-help"];

    [TestMethod]
    public void StartNamesTheVerboseProfileWithoutTheFileSuffixAndRecordsToAFile()
    {
        IReadOnlyList<string> args = WprCommand.Start(@"C:\ProgramData\Earshot\trace\x\BluetoothStack.wprp");

        CollectionAssert.AreEqual(ExpectedStart, args.ToArray());
        Assert.IsFalse(args[1].EndsWith(".File", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StopSavesToTheGivenFile()
    {
        CollectionAssert.AreEqual(ExpectedStop, WprCommand.Stop(@"C:\t\a.etl").ToArray());
    }

    [TestMethod]
    public void CancelAndHelpAreSingleWords()
    {
        CollectionAssert.AreEqual(ExpectedCancel, WprCommand.Cancel.ToArray());
        CollectionAssert.AreEqual(ExpectedHelp, WprCommand.Help.ToArray());
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
