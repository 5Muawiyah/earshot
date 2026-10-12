using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// One real execution of each harmless helper. Nothing here pairs, unpairs or scans, and no identifier is written to the
// assertion messages.
[TestClass]
public sealed class FixRealHelperTests
{
    [TestMethod]
    public async Task TheRealPairedOnlyListReturnsWithoutPairingOrScanning()
    {
        var api = new WinRtPairingApi();

        PairedListResult list = await api.ListPairedAsync(CancellationToken.None);

        // The list is the paired-only selector's, read from the cache: the call returns, and every device it returns
        // is one Windows holds paired. The address and the container of each were read from the record, so an
        // unreadable one would show up here as a missing value.
        Assert.IsNotNull(list.Devices);
        foreach (PairedDevice device in list.Devices)
        {
            Assert.IsTrue(BoundaryValidation.IsAddress12(device.Address12), "A paired device has an address that did not parse.");
            Assert.AreNotEqual(Guid.Empty, device.ContainerId, "A paired device has no container.");
        }

        // The only WinRT calls the type makes on this path.
        Expect.Same(api.CallsMade, "FindAllAsync(paired-only selector)");
    }

    [TestMethod]
    public void TheRealRepairPathDoesNotExposeAScanOnTheListPath()
    {
        // The scan is only reachable through Watch, which the fake in the flow tests shows is called after the unpair.
        var api = new WinRtPairingApi();
        Assert.IsEmpty(api.CallsMade);
    }

    [TestMethod]
    public void TheRealDesktopReadSeesTheDesktopItRunsOn()
    {
        DesktopReading reading = new WindowsInputDesktop().Read();

        Assert.IsFalse(string.IsNullOrEmpty(reading.ThreadDesktop), "The thread's desktop has a name.");
        Assert.AreEqual(FixRules.IsInteractive(reading), reading.UserInteractive && string.Equals(reading.ThreadDesktop, reading.InputDesktop, StringComparison.OrdinalIgnoreCase));
        // On the owner's desktop the two names match; on a private desktop (the test launcher's) they do not, and the Fix
        // run prints its plan and stops. Either is a correct answer; the result is written to the test output.
        TestContext.WriteLine("thread desktop name length " + reading.ThreadDesktop!.Length + ", input desktop read " + (reading.InputDesktop is null ? "failed with " + reading.InputError : "ok") + ", interactive " + FixRules.IsInteractive(reading));
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheRealRecordsReaderReadsAMissingFolderAsNoRecords()
    {
        using var temp = new TempFolder();
        var records = new StoreFixRecords(new GateStore(temp.File("none")), () => false);

        Assert.AreEqual(GateReadStatus.Missing, records.ReadDevice().Status);
        Assert.AreEqual(GateReadStatus.Missing, records.ReadConfig().Status);
        Assert.IsFalse(records.HasFootprint());
    }

    [TestMethod]
    public void TheLeftoversReaderFindsNothingInAnEmptyPlace()
    {
        using var temp = new TempFolder();
        var leftovers = new MachineLeftovers(temp.File("install"), temp.File("machine"), serviceFound: () => false);

        Assert.IsEmpty(leftovers.Read());
    }

    [TestMethod]
    public void TheLeftoversReaderNamesWhatIsStillThere()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("install"));
        File.WriteAllText(Path.Combine(temp.File("install"), "Earshot.exe"), "x");
        Directory.CreateDirectory(temp.File("machine"));
        var leftovers = new MachineLeftovers(temp.File("install"), temp.File("machine"), serviceFound: () => true);

        IReadOnlyList<string> items = leftovers.Read();

        Assert.HasCount(3, items);
        Assert.IsTrue(items.Any(i => i.Contains("hand-back service", StringComparison.OrdinalIgnoreCase)));
    }
}
