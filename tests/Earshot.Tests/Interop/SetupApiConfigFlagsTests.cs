using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// SetupAPI's SP_DEVINFO_DATA layout, the split between the reader and the writer, and one real read. Nothing here writes: the
// real writer is never constructed by a test, and the real reader has no way to reach it.
[TestClass]
public sealed class SetupApiConfigFlagsTests
{
    private static readonly string[] ReaderMethodNames = ["Read"];

    [TestMethod]
    public void DeviceInfoDataIs32BytesOnX64WithTheDevInstAt20()
    {
        Assert.AreEqual(32, Marshal.SizeOf<SP_DEVINFO_DATA>());
        Assert.AreEqual(0, Marshal.OffsetOf<SP_DEVINFO_DATA>(nameof(SP_DEVINFO_DATA.cbSize)).ToInt32());
        Assert.AreEqual(4, Marshal.OffsetOf<SP_DEVINFO_DATA>(nameof(SP_DEVINFO_DATA.ClassGuid)).ToInt32());
        Assert.AreEqual(20, Marshal.OffsetOf<SP_DEVINFO_DATA>(nameof(SP_DEVINFO_DATA.DevInst)).ToInt32());
        Assert.AreEqual(24, Marshal.OffsetOf<SP_DEVINFO_DATA>(nameof(SP_DEVINFO_DATA.Reserved)).ToInt32());
    }

    [TestMethod]
    public void TheRealReaderCannotWriteAndTheWriterIsAnotherInterface()
    {
        Assert.IsFalse(typeof(IConfigFlagsWriter).IsAssignableFrom(typeof(SetupApiConfigFlagsReader)), "The one real SetupAPI test must not be able to reach a write.");
        Assert.IsFalse(typeof(IConfigFlagsReader).IsAssignableFrom(typeof(SetupApiConfigFlagsWriter)));
        string[] readerMethods = typeof(SetupApiConfigFlagsReader).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToArray();
        CollectionAssert.AreEqual(ReaderMethodNames, readerMethods);
    }

    [TestMethod]
    public void TheSetupApiWritesAreReachedFromNowhereButTheGatesNodeApi()
    {
        // The only types that implement the write side. A test or a tray class that did would be a second way to a write.
        Type[] writers = typeof(IConfigFlagsWriter).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IConfigFlagsWriter).IsAssignableFrom(t))
            .ToArray();
        Assert.HasCount(1, writers);
        Assert.AreEqual(typeof(SetupApiConfigFlagsWriter), writers[0]);
    }

    // The one real execution: opens an instance that exists on any machine (the Bluetooth radio when there is one, a root
    // device otherwise) and reads its ConfigFlags through SetupAPI, unelevated. SetupDiOpenDeviceInfoW and
    // SetupDiGetDeviceRegistryPropertyW only read; nothing is changed and no Bluetooth device is queried.
    [TestMethod]
    public void TheRealReaderReadsTheConfigFlagsOfAnExistingInstance()
    {
        uint cr = CfgMgr32.GetDeviceIdList(null, CfgMgr32.CM_GETIDLIST_FILTER_PRESENT, out string[] ids);
        Assert.AreEqual(CfgMgr32.CR_SUCCESS, cr, "CM_Get_Device_ID_List returned " + cr);
        string? instance = ids.FirstOrDefault(id => id.StartsWith(@"BTH\MS_BTHBRB\", StringComparison.OrdinalIgnoreCase)) ??
                           ids.FirstOrDefault(id => id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(instance, "No device to read on this machine.");

        ConfigFlagsRead read = new SetupApiConfigFlagsReader().Read(instance);

        Assert.AreEqual(0u, read.Win32Error, "SetupAPI stopped at " + read.Stage + " with Win32 error " + read.Win32Error);
        Assert.AreEqual(ConfigFlagsStage.None, read.Stage);
        Assert.AreEqual(0u, read.Flags & CfgMgr32.CONFIGFLAG_DISABLED, "A present device that is working is not marked disabled.");
    }

    [TestMethod]
    public void AnInstanceThatDoesNotExistGivesTheRawErrorAtTheOpenStage()
    {
        ConfigFlagsRead read = new SetupApiConfigFlagsReader().Read(@"ROOT\NOSUCHENUMERATOR\0000");

        Assert.AreNotEqual(0u, read.Win32Error);
        Assert.AreEqual(ConfigFlagsStage.Open, read.Stage);
    }
}
