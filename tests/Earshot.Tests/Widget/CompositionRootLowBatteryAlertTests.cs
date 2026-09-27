using Earshot.Composition;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// CompositionRoot.BuildLowBatteryAlertService: proves, behaviourally rather than by decoding IL (there is
// nothing here for an IL walk to distinguish - both branches are an ordinary if/else, not two overloads of
// the same call), that safe mode and a redirected data root each compose a CardNotifier alone, never a real
// ToastNotifier, matching NotificationRegistration's own WritesBlocked facts. Never calls NotifyAsync on
// whatever comes back: constructing a ToastNotifier does not itself touch WinRT (only Show does), but this
// stays a pure composition check regardless.
[TestClass]
public sealed class CompositionRootLowBatteryAlertTests
{
    private const string DataRootVariable = "EARSHOT_DATA_ROOT";

    [TestMethod]
    public void InSafeModeTheNotifierIsTheCardAlone()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new ServiceRegistry(log, settings, action => action(), safeMode: true);
        WidgetStatusService status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System)!;

        using LowBatteryAlertService service = CompositionRoot.BuildLowBatteryAlertService(registry, status);

        Assert.IsInstanceOfType<CardNotifier>(service.NotifierForTest, "Safe mode must never construct a real ToastNotifier.");
        status.Dispose();
    }

    [TestMethod]
    public void WithARedirectedDataRootTheNotifierIsTheCardAlone()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new ServiceRegistry(log, settings, action => action(), safeMode: false);
        WidgetStatusService status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System)!;

        using LowBatteryAlertService service = CompositionRoot.BuildLowBatteryAlertService(registry, status);

        Assert.IsInstanceOfType<CardNotifier>(service.NotifierForTest, "A redirected data root must never construct a real ToastNotifier.");
        status.Dispose();
    }

    [TestMethod]
    public void WithSafeModeOffAndNoRedirectTheNotifierIsAToast()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, null); // clears any ambient redirect
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new ServiceRegistry(log, settings, action => action(), safeMode: false);
        WidgetStatusService status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System)!;

        using LowBatteryAlertService service = CompositionRoot.BuildLowBatteryAlertService(registry, status);

        Assert.IsInstanceOfType<ToastNotifier>(service.NotifierForTest, "Off safe mode and off a redirected data root, the real toast route must be used.");
        status.Dispose();
    }

    // Sets an environment variable for the life of one test and restores whatever the process had before.
    // A null value clears the variable rather than setting it. Duplicated from CompositionRootWidgetTests'
    // own private helper rather than shared, matching that file's own choice not to share it either.
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
