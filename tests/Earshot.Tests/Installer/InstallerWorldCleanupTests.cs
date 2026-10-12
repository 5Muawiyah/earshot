using System.Security;
using Microsoft.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Installer;

// The installer tests keep a run key of their own under HKCU and remove it afterwards. A removal that fails is reported with the
// exception's raw code, not dropped, so a key left behind shows in the test that left it.
[TestClass]
public sealed class InstallerWorldCleanupTests
{
    private static string NewTestKey() => @"Software\EarshotTests\" + Guid.NewGuid().ToString("N");

    private static void Remove(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

    [TestMethod]
    public void ARemovalThatIsRefusedIsReportedWithTheTypeAndTheRawCode()
    {
        string path = NewTestKey();
        try
        {
            using (Registry.CurrentUser.CreateSubKey(path))
            {
            }

            string? problem = InstallerWorld.RemoveEmptyKey(path, p => Registry.CurrentUser.OpenSubKey(p), _ => throw new UnauthorizedAccessException("Access to the registry key is denied."));

            Assert.IsNotNull(problem, "A refused delete must be reported.");
            StringAssert.Contains(problem, "UnauthorizedAccessException");
            StringAssert.Contains(problem, "0x80070005");
            StringAssert.Contains(problem, path);
        }
        finally
        {
            Remove(path);
        }
    }

    [TestMethod]
    public void AKeyThatCannotBeOpenedIsReported()
    {
        string? problem = InstallerWorld.RemoveEmptyKey(@"Software\EarshotTests\absent", _ => throw new SecurityException("No permission to read the key."), _ => { });

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "SecurityException");
    }

    [TestMethod]
    public void ARaceWithAnotherKeyBeingMadeIsReported()
    {
        string path = NewTestKey();
        try
        {
            using (Registry.CurrentUser.CreateSubKey(path))
            {
            }

            string? problem = InstallerWorld.RemoveEmptyKey(path, p => Registry.CurrentUser.OpenSubKey(p), _ => throw new InvalidOperationException("The key has subkeys."));

            Assert.IsNotNull(problem);
            StringAssert.Contains(problem, "InvalidOperationException");
        }
        finally
        {
            Remove(path);
        }
    }

    // The registry calls for real, against a key of the test's own.
    [TestMethod]
    public void AnEmptyKeyIsRemovedAKeyWithAChildStaysAndAMissingKeyIsFine()
    {
        string empty = NewTestKey();
        string full = NewTestKey();
        try
        {
            using (Registry.CurrentUser.CreateSubKey(empty))
            {
            }

            using (Registry.CurrentUser.CreateSubKey(full + @"\child"))
            {
            }

            Func<string, RegistryKey?> open = p => Registry.CurrentUser.OpenSubKey(p);
            Action<string> delete = p => Registry.CurrentUser.DeleteSubKey(p, throwOnMissingSubKey: false);

            Assert.IsNull(InstallerWorld.RemoveEmptyKey(empty, open, delete));
            using (RegistryKey? gone = Registry.CurrentUser.OpenSubKey(empty))
            {
                Assert.IsNull(gone, "The empty key should have been removed.");
            }

            Assert.IsNull(InstallerWorld.RemoveEmptyKey(full, open, delete));
            using (RegistryKey? stays = Registry.CurrentUser.OpenSubKey(full))
            {
                Assert.IsNotNull(stays, "A key that holds another key stays.");
            }

            Assert.IsNull(InstallerWorld.RemoveEmptyKey(NewTestKey(), open, delete));
        }
        finally
        {
            Remove(empty);
            Remove(full);
        }
    }
}
