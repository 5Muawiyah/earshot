using Earshot.Composition;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// CompositionRoot.BuildWidget: the widget's data pipeline, gated by WidgetSettings.Enabled. A real
// JsonSettingsStore over a private TempFolder, not FakeSettingsStore (Phase2Fakes): that fake's Copy()
// predates the v1.1 members and drops Widget on every read, which would make every case here look like
// the setting was off. EARSHOT_DATA_ROOT is redirected to the same folder for the life of each test, so
// BuildWidget's ClaimStore (Paths.Current.WidgetClaimFile) never reaches the owner's real
// %LOCALAPPDATA%\Earshot.
[TestClass]
public sealed class CompositionRootWidgetTests
{
    private const string DataRootVariable = "EARSHOT_DATA_ROOT";

    [TestMethod]
    public void WithTheWidgetOffNothingIsBuilt()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = false });
        var registry = new Earshot.Composition.ServiceRegistry(log, settings, action => action(), safeMode: false);

        IWidgetStatus? status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System);

        Assert.IsNull(status);
        Assert.IsNull(registry.WidgetStatus);
        Assert.IsNull(registry.MediaSessions);
    }

    [TestMethod]
    public void InSafeModeMediaSessionsIsWrapped()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new Earshot.Composition.ServiceRegistry(log, settings, action => action(), safeMode: true);

        IWidgetStatus? status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System);

        Assert.IsNotNull(status);
        Assert.IsNotNull(registry.WidgetStatus);
        Assert.IsInstanceOfType<SafeMediaSessions>(registry.MediaSessions);
    }

    [TestMethod]
    public void WithTheWidgetOnAndSafeModeOffMediaSessionsIsNotWrapped()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new Earshot.Composition.ServiceRegistry(log, settings, action => action(), safeMode: false);

        IWidgetStatus? status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System);

        Assert.IsNotNull(status);
        Assert.IsNotInstanceOfType<SafeMediaSessions>(registry.MediaSessions);
    }

    // Sets an environment variable for the life of one test and restores whatever the process had before.
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    // The proof store is built under the widget folder of the data root: its summary is written at start, and the
    // set-up records it reads live beside it.
    [TestMethod]
    public void BuildWidgetConstructsTheProofStoreUnderTheWidgetFolder()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new Earshot.Composition.ServiceRegistry(log, settings, action => action(), safeMode: true);
        Paths paths = Paths.Current;

        IWidgetStatus? status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System);

        Assert.IsNotNull(status);
        string proofFile = Path.Combine(paths.WidgetFolder, "proof.json");
        Assert.AreEqual(proofFile, CompositionRoot.WidgetProofFile(paths));
        Assert.IsTrue(File.Exists(proofFile), "The summary is written when the store is built: " + proofFile);
        Assert.IsTrue(proofFile.StartsWith(temp.Path, StringComparison.OrdinalIgnoreCase), "Under the redirected data root, never the real profile.");
    }
}
