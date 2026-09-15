using System.Globalization;
using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// The real monitor against the real audio stack, read-only: it registers for notifications, enumerates, walks
// the topology of each device to its filters and reads each filter's state and container. It never sends a
// kernel streaming request, never sets a property and never changes a devnode or service. Inconclusive when
// the machine has no Bluetooth audio endpoint.
[TestClass]
[TestCategory("ReadOnlyHardware")]
public sealed class MonitorReadOnlyHardwareTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheRealMonitorGroupsEndpointsByContainerConsistently()
    {
        var log = new CapturingLog();
        var settings = new FakeSettingsStore();
        var worker = new AudioWorker(log);
        var monitor = new CoreAudioDeviceMonitor(worker, new CoreAudioEndpointSource(worker), settings, log, action => action());
        var firstSnapshot = new TaskCompletionSource<DeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, e) => firstSnapshot.TrySetResult(e.Snapshot);
        try
        {
            monitor.Start();

            // A machine with no endpoints at all raises nothing, so the wait is bounded and a refresh follows.
            Task finished = await Task.WhenAny(firstSnapshot.Task, Task.Delay(Timeout));
            MonitorRefresh refresh = await monitor.RefreshDetailedAsync().WaitAsync(Timeout);

            foreach (StepOutcome step in refresh.Steps)
            {
                TestContext.WriteLine("step: " + CoreAudioDeviceMonitor.Describe(step));
            }

            Assert.IsTrue(refresh.EnumerationOk, "The enumeration failed.");
            Assert.IsTrue(log.Has(LogLevel.Info, "Watching audio devices for changes."), "The notification client did not register.");
            if (finished == firstSnapshot.Task)
            {
                AssertConsistent(firstSnapshot.Task.Result, null);
            }

            DeviceSnapshot snapshot = refresh.Snapshot;
            AssertConsistent(snapshot, refresh.Readings);

            List<GroupFilters> filters = await worker.RunAsync(_ => ReadFilters(worker, snapshot)).WaitAsync(Timeout);
            foreach (GroupFilters group in filters)
            {
                TestContext.WriteLine("group " + TopologyWalk.Format(group.Group.ContainerId) + " \"" + group.Group.DisplayName + "\" " + group.Group.Connection);
                foreach (FilterVisit visit in group.Visits)
                {
                    TestContext.WriteLine("  adapter " + visit.Adapter.AdapterId + " state " + visit.State + " container " +
                                          (visit.ContainerId is Guid c ? TopologyWalk.Format(c) : "not read"));
                }
            }

            List<GroupFilters> bluetooth = filters.Where(g => g.Visits.Any(v => IsBluetoothAdapter(v.Adapter.AdapterId))).ToList();
            if (bluetooth.Count == 0)
            {
                Assert.Inconclusive("No Bluetooth audio endpoint on this machine.");
            }

            foreach (GroupFilters group in bluetooth)
            {
                // Every present filter a Bluetooth device's endpoints lead to belongs to that device's container.
                foreach (FilterVisit visit in group.Visits.Where(v => v.ContainerId is not null))
                {
                    Assert.AreEqual(group.Group.ContainerId, visit.ContainerId, visit.Adapter.AdapterId);
                }
            }

            if (snapshot.Target is DeviceModel target)
            {
                TestContext.WriteLine("target " + TopologyWalk.Format(target.ContainerId) + " \"" + target.DisplayName + "\" " + target.Connection);
            }
        }
        finally
        {
            monitor.Dispose();
            await worker.DisposeAsync();
        }

        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(Environment.NewLine, log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message)));
    }

    private static void AssertConsistent(DeviceSnapshot snapshot, IReadOnlyList<EndpointReading>? readings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var containers = new HashSet<Guid>();
        foreach (DeviceModel group in snapshot.AllGroups)
        {
            Assert.IsTrue(containers.Add(group.ContainerId), "Container " + group.ContainerId + " appears in two groups.");
            Assert.IsNotEmpty(group.Endpoints, "A group without endpoints.");
            foreach (AudioEndpoint endpoint in group.Endpoints)
            {
                Assert.AreEqual(group.ContainerId, endpoint.ContainerId, endpoint.EndpointId);
                Assert.IsTrue(seen.Add(endpoint.EndpointId), "Endpoint " + endpoint.EndpointId + " appears twice.");
            }

            Assert.AreEqual(EndpointModelBuilder.DeriveConnection(group.Endpoints), group.Connection, group.ContainerId.ToString("B", CultureInfo.InvariantCulture));
        }

        if (readings is not null)
        {
            CollectionAssert.AreEquivalent(readings.Select(r => r.Endpoint.EndpointId).ToList(), seen.ToList());
        }

        if (snapshot.Target is DeviceModel target)
        {
            Assert.IsTrue(NodeMatch.IsValidTargetContainer(target.ContainerId), "The target container is never a valid target.");
            Assert.IsTrue(snapshot.AllGroups.Contains(target), "The target is not one of the groups.");
        }
    }

    // Worker thread only. For each device that can be a target, the filters its endpoints lead to, with their
    // state and container. IKsControl is activated for filters that pass the guard and released unused.
    private static List<GroupFilters> ReadFilters(AudioWorker worker, DeviceSnapshot snapshot)
    {
        int hr = worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        Assert.IsTrue(hr >= 0 && enumerator is not null, "The enumerator could not be created: " + NativeCodes.Name(hr));

        var result = new List<GroupFilters>();
        foreach (DeviceModel group in snapshot.AllGroups.Where(g => NodeMatch.IsValidTargetContainer(g.ContainerId)))
        {
            AdapterDiscovery discovery = TopologyWalk.FindAdapters(enumerator, group.Endpoints, group.ContainerId);
            IReadOnlyList<FilterVisit> visits = TopologyWalk.VisitFilters(enumerator, discovery.Adapters, group.ContainerId, static (_, _) => Array.Empty<StepOutcome>());
            result.Add(new GroupFilters(group, visits));
        }

        return result;
    }

    private static bool IsBluetoothAdapter(string adapterId) =>
        adapterId.Contains("\\bthenum#", StringComparison.OrdinalIgnoreCase) ||
        adapterId.Contains("\\bthhfenum#", StringComparison.OrdinalIgnoreCase);

    private sealed record GroupFilters(DeviceModel Group, IReadOnlyList<FilterVisit> Visits);
}
