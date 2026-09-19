using Earshot.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

[TestClass]
public sealed class VirtualKeyTableTests
{
    [TestMethod]
    public void VirtualKeyTableHasNoDuplicateVirtualKeys()
    {
        var seen = new HashSet<ushort>();
        foreach (string name in VirtualKeyTable.KeyNames)
        {
            Assert.IsTrue(VirtualKeyTable.TryGetVirtualKey(name, out ushort virtualKey), name);
            Assert.IsTrue(seen.Add(virtualKey), name + " reuses a virtual key already claimed by another name.");
            Assert.IsTrue(VirtualKeyTable.TryGetKeyName(virtualKey, out string roundTripped), name);
            Assert.AreEqual(name, roundTripped);
        }

        foreach (ushort unnamed in new ushort[] { 0, 0x07, 0x3A })
        {
            Assert.IsFalse(VirtualKeyTable.TryGetKeyName(unnamed, out _), unnamed.ToString());
        }
    }
}
