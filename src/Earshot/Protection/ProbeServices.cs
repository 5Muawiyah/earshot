using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// probe services: the installed Bluetooth services of the pinned device, read without elevation, with
// Handsfree (0000111E), Headset (00001108) and A2DP sink (0000110B) called out, the protection state they
// give, and what protection.json and protection-intent.json hold. It only enumerates and reads: nothing is
// turned on or off and nothing is written.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothenumerateinstalledservices
internal static partial class Program
{
    internal sealed record ServicesProbeReport(
        string SettingsAddress,
        string DeviceFile,
        string? DeviceAddress,
        string? UsedAddress,
        string? UsedFrom,
        ServiceReadResult? Read,
        AudioProtectionSnapshot Snapshot,
        string RecordFile,
        IReadOnlyList<Guid>? RecordedServices,
        string IntentFile,
        bool? PendingProtect,
        IReadOnlyList<StepOutcome> Steps);

    static partial void ProbeServices(ProbeContext ctx)
    {
        EarshotSettings settings = ctx.Services.Settings.Current;
        var store = new GateStore(Paths.Current.MachineFolder);
        ServicesProbeReport report = ReadServicesProbe(new BluetoothServiceReader(), settings, store);
        WriteServicesProbe(ctx, report);
        ctx.Handled = true;
        ctx.ExitCode = report.UsedAddress is null ? ExitCodes.Config
            : report.Read is { DeviceFound: true, Listed: true } ? ExitCodes.Ok
            : ExitCodes.OsError;
    }

    internal static ServicesProbeReport ReadServicesProbe(IBluetoothServiceReader api, EarshotSettings settings, GateStore store)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        var steps = new List<StepOutcome>();

        GateRead<DeviceIdentity> device = store.ReadDevice();
        var pinned = new DeviceIdentity { Address = settings.PinnedAddress, ContainerId = settings.PinnedContainerId };
        DeviceIdentity? used = device.IsOk ? device.Value : GateStore.ValidateDevice(pinned) is null ? pinned : null;
        string? usedFrom = device.IsOk ? "device.json" : used is null ? null : "settings";
        AddUnlessOkOrMissing(steps, device.Status, device.Step);

        GateRead<ProtectionRecord> record = store.ReadProtection();
        AddUnlessOkOrMissing(steps, record.Status, record.Step);
        GateRead<ProtectionIntent> intent = new ProtectionIntentFile(store.Folder).Read();
        AddUnlessOkOrMissing(steps, intent.Status, intent.Step);

        ServiceReadResult? read = null;
        AudioProtectionSnapshot snapshot = new(AudioProtectionState.Unknown, HandsfreeInstalled: false, HeadsetInstalled: false);
        if (used is not null)
        {
            read = new ServiceStateReader(api).Read(used.Address);
            steps.AddRange(read.Steps);
            snapshot = ProtectionClassifier.Snapshot(read);
        }

        return new ServicesProbeReport(
            settings.PinnedAddress, device.Status.ToString(), device.Value?.Address, used?.Address, usedFrom,
            read, snapshot, record.Status.ToString(), record.Value?.DisabledServices, intent.Status.ToString(),
            intent.Value?.Protect, steps);
    }

    internal static void WriteServicesProbe(ProbeContext ctx, ServicesProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(report);
        ServiceReadResult? read = report.Read;
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "services");
                w.WriteString("settingsAddress", report.SettingsAddress);
                w.WriteString("deviceFile", report.DeviceFile);
                w.WriteString("usedFrom", report.UsedFrom);
                w.WriteString("address", report.UsedAddress);
                w.WriteBoolean("deviceFound", read?.DeviceFound ?? false);
                w.WriteString("deviceName", read?.DeviceName);
                w.WriteBoolean("connected", read?.Connected ?? false);
                w.WriteBoolean("listed", read?.Listed ?? false);
                w.WriteBoolean("complete", read?.Complete ?? false);
                w.WriteStartArray("installedServices");
                foreach (Guid service in read?.Services ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("guid", service.ToString("D"));
                    w.WriteString("label", KnownServiceLabel(service));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteServiceFlag(w, "handsfree0000111E", read, ProtectedServices.Handsfree);
                WriteServiceFlag(w, "headset00001108", read, ProtectedServices.Headset);
                WriteServiceFlag(w, "a2dpSink0000110B", read, ProtectedServices.AudioSink);
                w.WriteString("protection", report.Snapshot.State.ToString());
                w.WriteString("protectionRecord", report.RecordFile);
                w.WriteStartArray("recordedServices");
                foreach (Guid service in report.RecordedServices ?? [])
                {
                    w.WriteStringValue(service.ToString("D"));
                }

                w.WriteEndArray();
                w.WriteString("protectionIntent", report.IntentFile);
                if (report.PendingProtect is bool pending)
                {
                    w.WriteBoolean("pendingProtect", pending);
                }
                else
                {
                    w.WriteNull("pendingProtect");
                }

                WriteSteps(w, "steps", report.Steps);
                w.WriteEndObject();
            });
            return;
        }

        TextWriter o = ctx.Out;
        o.WriteLine("Pinned in settings: address " + (report.SettingsAddress.Length == 0 ? "none" : report.SettingsAddress));
        o.WriteLine("Gate identity (device.json): " + (report.DeviceAddress ?? report.DeviceFile));
        if (report.UsedAddress is null || read is null)
        {
            o.WriteLine("No valid device is pinned, so there is nothing to read.");
            return;
        }

        o.WriteLine("Reading address " + report.UsedAddress + " (from " + report.UsedFrom + ")");
        if (!read.DeviceFound)
        {
            o.WriteLine("Device: not found among the paired Bluetooth devices");
        }
        else
        {
            o.WriteLine("Device: \"" + read.DeviceName + "\", connected " + (read.Connected ? "yes" : "no"));
            o.WriteLine("Installed services: " + (read.Listed
                ? read.Services.Count.ToString(CultureInfo.InvariantCulture) + (read.Complete ? ", complete list" : ", incomplete list")
                : "unreadable"));
            foreach (Guid service in read.Services)
            {
                string label = KnownServiceLabel(service);
                o.WriteLine("  " + service.ToString("D").ToUpperInvariant() + (label.Length == 0 ? "" : "  " + label));
            }
        }

        o.WriteLine("Handsfree 0000111E: " + ServiceFlag(read, ProtectedServices.Handsfree));
        o.WriteLine("Headset   00001108: " + ServiceFlag(read, ProtectedServices.Headset));
        o.WriteLine("A2DP sink 0000110B: " + ServiceFlag(read, ProtectedServices.AudioSink));
        o.WriteLine("Protection: " + report.Snapshot.State);
        o.WriteLine("protection.json: " + report.RecordFile +
                    (report.RecordedServices is { } recorded
                        ? ", turned off by Earshot: " + (recorded.Count == 0 ? "none" : string.Join(", ", recorded.Select(ProtectedServices.Label)))
                        : ""));
        o.WriteLine("protection-intent.json: " + report.IntentFile +
                    (report.PendingProtect is bool pending ? ", waiting to turn protection " + (pending ? "on" : "off") : ""));
        foreach (StepOutcome step in report.Steps)
        {
            o.WriteLine("  " + GateActions.Describe(step));
        }
    }

    private static void AddUnlessOkOrMissing(List<StepOutcome> steps, GateReadStatus status, StepOutcome step)
    {
        if (status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
        {
            steps.Add(step);
        }
    }

    // A label for the services protection is about; others are listed by GUID only.
    private static string KnownServiceLabel(Guid service) =>
        service == ProtectedServices.Handsfree ? "Handsfree (protection turns it off)"
        : service == ProtectedServices.Headset ? "Headset (protection turns it off)"
        : service == ProtectedServices.AudioSink ? "A2DP sink (always left on)"
        : "";

    private static string ServiceFlag(ServiceReadResult read, Guid service) =>
        !read.DeviceFound || !read.Listed ? "unknown"
        : read.Services.Contains(service) ? "installed"
        : read.Complete ? "not installed"
        : "not in the incomplete list";

    private static void WriteServiceFlag(Utf8JsonWriter w, string name, ServiceReadResult? read, Guid service)
    {
        if (read is null || !read.DeviceFound || !read.Listed || (!read.Complete && !read.Services.Contains(service)))
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteBoolean(name, read.Services.Contains(service));
        }
    }
}
