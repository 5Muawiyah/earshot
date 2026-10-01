using Earshot.Composition;
using Earshot.Infra;
using Earshot.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// CompositionRoot.BuildAutoPauseService: proves it constructs the real AutoPause/AutoPauseService wiring
// without throwing given a normal, widget-enabled registry (BuildWidget having already set r.MediaSessions,
// the one thing BuildAutoPauseService requires), the same shape CompositionRootLowBatteryAlertTests uses for
// BuildLowBatteryAlertService. Never drives a reading through it: that is AutoPauseServiceTests' job.
[TestClass]
public sealed class CompositionRootAutoPauseTests
{
    private const string DataRootVariable = "EARSHOT_DATA_ROOT";

    [TestMethod]
    public void BuildsWithoutThrowingOnceBuildWidgetHasSetMediaSessions()
    {
        using var temp = new TempFolder();
        using var dataRoot = new EnvironmentVariableScope(DataRootVariable, temp.Path);
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new ServiceRegistry(log, settings, action => action(), safeMode: true);
        WidgetStatusService status = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System)!;

        using AutoPauseService service = CompositionRoot.BuildAutoPauseService(registry, status, () => null, TimeProvider.System);

        Assert.IsNotNull(service);
        status.Dispose();
    }

    [TestMethod]
    public void ThrowsWhenMediaSessionsWasNeverSet()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        var registry = new ServiceRegistry(log, settings, action => action(), safeMode: true);

        // BuildWidget never ran (the widget stayed disabled, say), so registry.MediaSessions is still null:
        // a genuine upstream bug this must surface loudly, not silently no-op past.
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            CompositionRoot.BuildAutoPauseService(registry, new NullWidgetStatusForTest(), () => null, TimeProvider.System));
    }

    // A minimal IWidgetStatus so ThrowsWhenMediaSessionsWasNeverSet can reach BuildAutoPauseService's own
    // null check without needing a real WidgetStatusService (BuildWidget is exactly what this test says never
    // ran).
    private sealed class NullWidgetStatusForTest : IWidgetStatus
    {
        public WidgetSnapshot Current => WidgetSnapshot.Empty(WidgetWatcherState.NotStarted);


        public event EventHandler? Changed;

        public event EventHandler<CaseOpenedEventArgs>? CaseOpened;

        public event EventHandler<ReadingAppliedEventArgs>? ReadingApplied;




        public Task RefreshAsync() => Task.CompletedTask;

        // Kept only so the field-like events above count as used.
        internal void RaiseUnused()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            CaseOpened?.Invoke(this, new CaseOpenedEventArgs(DateTimeOffset.UtcNow));
            ReadingApplied?.Invoke(this, new ReadingAppliedEventArgs(DecodedReading0, DateTimeOffset.UtcNow));
        }

        private static readonly DecodedReading DecodedReading0 = new(PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, null, null);
    }

    // Sets an environment variable for the life of one test and restores whatever the process had before.
    // Duplicated from CompositionRootLowBatteryAlertTests' own private helper rather than shared, matching
    // that file's own choice not to share it either.
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
