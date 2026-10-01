using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Earshot.Battery;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The one execution of the real Hands-Free battery read against this machine: the real CfgMgr32 reader and the real
// device query, read-only, for the device pinned in the real settings file (read here, never written). It is
// inconclusive where it has nothing to run on (no settings file, no pinned device, no Bluetooth adapter, as on the
// hosted build). What it found is written to the test output as a count and a note, never an identifier.
[TestClass]
public sealed class HandsFreeBatteryRealQueryTests
{
    public TestContext TestContext { get; set; } = null!;

    // The real reader with a stopwatch on each kind of call, so the output says where the time went.
    private sealed class TimedReader(IBatterySweepReader inner) : IBatterySweepReader
    {
        public Dictionary<string, (int Calls, long Ms)> Spent { get; } = [];

        private T Time<T>(string name, Func<T> call)
        {
            long start = Stopwatch.GetTimestamp();
            T result = call();
            (int calls, long ms) = Spent.GetValueOrDefault(name);
            Spent[name] = (calls + 1, ms + (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }

        public uint ListDeviceIds(out string[] ids)
        {
            string[] captured = [];
            uint cr = Time("list device ids", () => inner.ListDeviceIds(out captured));
            ids = captured;
            return cr;
        }

        public uint Locate(string instanceId, out uint devInst)
        {
            uint captured = 0;
            uint cr = Time("locate", () => inner.Locate(instanceId, out captured));
            devInst = captured;
            return cr;
        }

        public uint GetPropertyKeys(uint devInst, out DEVPROPKEY[] keys) => inner.GetPropertyKeys(devInst, out keys);

        public uint GetProperty(uint devInst, DEVPROPKEY key, out uint type, out byte[] data)
        {
            uint capturedType = 0;
            byte[] capturedData = [];
            uint cr = Time("get property", () => inner.GetProperty(devInst, key, out capturedType, out capturedData));
            type = capturedType;
            data = capturedData;
            return cr;
        }

        public bool IsPresent(string instanceId) => inner.IsPresent(instanceId);

        public int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects)
        {
            IReadOnlyList<DevObjectRecord> captured = [];
            int hr = Time("paired objects (type " + objectType.ToString(CultureInfo.InvariantCulture) + ")", () => inner.GetPairedObjects(objectType, out captured));
            objects = captured;
            return hr;
        }
    }

    private static string RealSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Earshot", "settings.json");

    [TestMethod]
    public async Task TheRealReadReturnsWithoutThrowingAndEveryFailureCarriesItsRawCode()
    {
        if (!File.Exists(RealSettingsPath))
        {
            Assert.Inconclusive("There is no settings file on this machine, so no device is pinned.");
        }

        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(RealSettingsPath));
        Guid container = settings.RootElement.TryGetProperty("PinnedContainerId", out JsonElement c) && Guid.TryParse(c.GetString(), out Guid parsed) ? parsed : Guid.Empty;
        string address = settings.RootElement.TryGetProperty("PinnedAddress", out JsonElement a) ? a.GetString() ?? "" : "";
        if (!NodeMatch.IsValidTargetContainer(container) || !BoundaryValidation.IsAddress12(address))
        {
            Assert.Inconclusive("The settings file pins no device.");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063) || await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync().AsTask() is null)
        {
            Assert.Inconclusive("This machine has no Bluetooth adapter.");
        }

        var timed = new TimedReader(new SystemBatterySweepReader());
        var provider = new HandsFreeBatteryProvider(timed, () => address);
        var watch = Stopwatch.StartNew();

        HandsFreeBatteryRead read = provider.Read(container, address);

        watch.Stop();
        TestContext.WriteLine("Windows' Hands-Free battery, read-only, once: " + (read.Percent is int percent
            ? percent.ToString(CultureInfo.InvariantCulture) + "% from a " + read.Origin
            : "no figure. " + read.Note));
        foreach ((string name, (int calls, long ms)) in timed.Spent)
        {
            TestContext.WriteLine("  " + name + ": " + calls.ToString(CultureInfo.InvariantCulture) + " calls, " + ms.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        TestContext.WriteLine("Steps that failed: " + read.Steps.Count(s => !s.Ok) + " of " + read.Steps.Count + ". Took " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
        foreach (StepOutcome step in read.Steps.Where(s => !s.Ok))
        {
            TestContext.WriteLine("  " + step.Step + " " + step.CodeName);
            Assert.IsFalse(string.IsNullOrEmpty(step.CodeName), "A failing step carries its raw code: " + step.Step);
        }

        Assert.IsTrue(read.Percent is null || (read.Percent >= 0 && read.Percent <= 100));
        Assert.IsTrue(read.Percent is not null || read.Note is not null, "No figure always says why.");
    }
}
