using Earshot.App;
using Earshot.Composition;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// probe and diag build their services through RunServices. Probe must change nothing, so building
// the services must not create, rewrite or move the settings file.
[TestClass]
public sealed class RunServicesTests
{
    private static Paths PathsUnder(string root) =>
        Paths.FromEnvironment(name => name switch
        {
            Paths.DataRootVariable => root,
            Paths.SafeModeVariable => "1",
            _ => null,
        });

    [TestMethod]
    public void BuildingTheServicesDoesNotCreateSettings()
    {
        using var temp = new TempFolder();
        Paths paths = PathsUnder(temp.Path);
        var log = new CapturingLog();

        using (var services = new RunServices(paths, log))
        {
            ServiceRegistry registry = services.Get();
            Assert.IsTrue(services.IsBuilt);
            Assert.AreEqual("AirPods", registry.Settings.Current.DeviceMatch);
        }

        Assert.IsFalse(File.Exists(paths.SettingsFile));
        Assert.IsEmpty(Directory.GetFileSystemEntries(temp.Path));
    }

    [TestMethod]
    public void BuildingTheServicesLeavesAnUnusableSettingsFileWhereItIs()
    {
        using var temp = new TempFolder();
        Paths paths = PathsUnder(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        File.WriteAllText(paths.SettingsFile, "not json");
        var log = new CapturingLog();

        using (var services = new RunServices(paths, log))
        {
            services.Get();
        }

        Assert.AreEqual("not json", File.ReadAllText(paths.SettingsFile));
        string[] files = Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile)!);
        Assert.HasCount(1, files);
        Assert.AreEqual(paths.SettingsFile, files[0]);
    }
}
