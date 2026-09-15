using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Infra;

[TestClass]
public sealed class PathsTests
{
    private static Func<string, string?> Env(string? dataRoot = null, string? safeMode = null) =>
        name => name switch
        {
            Paths.DataRootVariable => dataRoot,
            Paths.SafeModeVariable => safeMode,
            _ => null,
        };

    private static string Special(Environment.SpecialFolder folder) =>
        Path.Combine(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify), "Earshot");

    [TestMethod]
    public void WithoutARedirectTheRealFoldersAreUsed()
    {
        Paths paths = Paths.FromEnvironment(Env());

        Assert.IsNull(paths.DataRoot);
        Assert.IsFalse(paths.IsRedirected);
        Assert.AreEqual(Special(Environment.SpecialFolder.ApplicationData), paths.RoamingFolder);
        Assert.AreEqual(Special(Environment.SpecialFolder.LocalApplicationData), paths.LocalFolder);
        Assert.AreEqual(Special(Environment.SpecialFolder.CommonApplicationData), paths.MachineFolder);
        Assert.AreEqual(Special(Environment.SpecialFolder.ProgramFiles), paths.InstallFolder);
        Assert.AreEqual(Path.Combine(paths.LocalFolder, "livetest"), paths.LiveTestFolder);
        Assert.AreEqual(Path.Combine(paths.RoamingFolder, "settings.json"), paths.SettingsFile);
    }

    [TestMethod]
    public void DataRootRedirectsTheThreeDataFoldersOnly()
    {
        using var temp = new TempFolder();

        Paths paths = Paths.FromEnvironment(Env(dataRoot: temp.Path));

        Assert.AreEqual(temp.Path, paths.DataRoot);
        Assert.IsTrue(paths.IsRedirected);
        Assert.AreEqual(Path.Combine(temp.Path, "Roaming", "Earshot"), paths.RoamingFolder);
        Assert.AreEqual(Path.Combine(temp.Path, "Local", "Earshot"), paths.LocalFolder);
        Assert.AreEqual(Path.Combine(temp.Path, "Local", "Earshot", "livetest"), paths.LiveTestFolder);
        Assert.AreEqual(Path.Combine(temp.Path, "Local", "Earshot", "logs"), paths.LogFolder);
        Assert.AreEqual(Path.Combine(temp.Path, "ProgramData", "Earshot"), paths.MachineFolder);
        Assert.AreEqual(Special(Environment.SpecialFolder.ProgramFiles), paths.InstallFolder);

        foreach (string file in new[] { paths.SettingsFile, paths.LogFile, paths.GateConfigFile, paths.DeviceIdentityFile, paths.ProtectionRecordFile })
        {
            Assert.IsTrue(file.StartsWith(temp.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), file);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankDataRootIsIgnored(string value)
    {
        Assert.IsFalse(Paths.FromEnvironment(Env(dataRoot: value)).IsRedirected);
    }

    [TestMethod]
    [DataRow("relative\\folder")]
    [DataRow("C:folder")]
    [DataRow("\\folder")]
    public void RelativeDataRootFailsClosed(string value)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Paths.FromEnvironment(Env(dataRoot: value)));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("0", false)]
    [DataRow("false", false)]
    [DataRow("FALSE", false)]
    [DataRow("1", true)]
    [DataRow(" 1 ", true)]
    [DataRow("true", true)]
    [DataRow("yes", true)]
    public void SafeModeErrsTowardsOn(string? value, bool expected)
    {
        Assert.AreEqual(expected, Paths.FromEnvironment(Env(safeMode: value)).IsSafeMode);
    }

    [TestMethod]
    public void CurrentReadsTheProcessEnvironment()
    {
        using var temp = new TempFolder();
        using (new EnvironmentVariableScope(Paths.DataRootVariable, temp.Path))
        using (new EnvironmentVariableScope(Paths.SafeModeVariable, "1"))
        {
            Paths paths = Paths.Current;
            Assert.AreEqual(Path.Combine(temp.Path, "Roaming", "Earshot", "settings.json"), paths.SettingsFile);
            Assert.IsTrue(paths.IsSafeMode);
        }
    }

    [TestMethod]
    public void StatusFileNeedsAValidNonce()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(Env(dataRoot: temp.Path));

        Assert.AreEqual(
            Path.Combine(paths.MachineFolder, "status-0123456789abcdef0123456789abcdef.json"),
            paths.StatusFile("0123456789abcdef0123456789abcdef"));

        foreach (string bad in new[] { "", "..\\..\\x", "0123456789ABCDEF0123456789ABCDEF", "$(Arg1)" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => paths.StatusFile(bad));
        }
    }
}
