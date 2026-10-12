using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Earshot.Boot;
using Earshot.Contracts;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Earshot.Repair;

// Removing the AirPods from Windows and pairing them again, in the owner's session and only after the second on-screen OK.
//
// All WinRT calls sit behind IPairingApi so every call is faked in the tests. The page that lists WinRT methods desktop
// apps cannot use names DeviceInformationPairing.PairAsync (the one that raises Windows' own pairing UI) and no other pairing
// method; this code uses DeviceInformationCustomPairing.PairAsync and UnpairAsync, which that page does not list. Whether
// either works from this program on this PC is proved only by the owner's run, and so is whether UnpairAsync removes the
// device's nodes and flags the way Settings' Remove device does. Every status either returns is recorded and shown.
// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationpairing.unpairasync
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationcustompairing.pairasync
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicepairingresultstatus
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceunpairingresultstatus

internal enum FixPairKind
{
    ConfirmOnly,
    ConfirmPinMatch,
    DisplayPin,
    Other,
}

internal sealed record PairedDevice(string Id, string Name, string Address12, Guid ContainerId);

internal sealed record UnpairedDevice(string Id, string Name, string Address12, bool IsAudioVideo);

internal enum WatchEventKind
{
    Added,
    Updated,
    Removed,
    InitialListDone,
}

internal sealed record WatchEvent(WatchEventKind Kind, UnpairedDevice? Device);

internal sealed record PairedListResult(IReadOnlyList<PairedDevice> Devices, IReadOnlyList<StepOutcome> Steps);

// A status Windows returned: the enum name, its number, and whether it means done.
internal sealed record PairingCallResult(string Name, int Code, bool Success, string? Detail);

internal interface IPairingPrompt
{
    Task<bool> ConfirmAsync(FixPairKind kind, string? pin);
}

// A scan in progress. Events are produced only while EventsAsync is being read, and the scan stops when this is disposed.
internal interface IUnpairedWatch : IDisposable
{
    IAsyncEnumerable<WatchEvent> EventsAsync(CancellationToken ct);
}

internal interface IPairingApi
{
    // The paired-only list, from the cache: no inquiry.
    Task<PairedListResult> ListPairedAsync(CancellationToken ct);

    Task<PairingCallResult> UnpairAsync(string deviceId, CancellationToken ct);

    // A watcher over unpaired classic devices. Creating it does nothing; reading its events starts a scan.
    IUnpairedWatch Watch();

    Task<PairingCallResult> PairAsync(string deviceId, IPairingPrompt prompt, CancellationToken ct);
}

internal static partial class FixPlatform
{
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    internal static bool HasDeviceEnumeration { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}

// The real API. Properties are asked for by name so a device is matched by its Bluetooth address and container, never by its
// name.
// https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties
internal sealed partial class WinRtPairingApi : IPairingApi
{
    internal const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    internal const string ContainerProperty = "System.Devices.Aep.ContainerId";
    internal const string MajorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Major";

    // Class of device, major class: Audio/Video.
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothmajorclass
    private const uint AudioVideoMajorClass = 4;

    private static readonly string[] RequestedProperties = [AddressProperty, ContainerProperty, MajorClassProperty];

    [GeneratedRegex(@"(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex MacInId();

    // The WinRT calls made, by name, for a test to read. Reading the list adds one entry and nothing else.
    public List<string> CallsMade { get; } = new();

    public async Task<PairedListResult> ListPairedAsync(CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        if (!FixPlatform.HasDeviceEnumeration)
        {
            steps.Add(StepOutcomes.NotAvailable("pair-list", "Windows is older than 10.0.19041."));
            return new PairedListResult([], steps);
        }

        return await ListPairedGuardedAsync(steps, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private async Task<PairedListResult> ListPairedGuardedAsync(List<StepOutcome> steps, CancellationToken ct)
    {
        var devices = new List<PairedDevice>();
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            CallsMade.Add("FindAllAsync(paired-only selector)");
            DeviceInformationCollection found = await DeviceInformation.FindAllAsync(selector, RequestedProperties).AsTask(ct).ConfigureAwait(false);
            foreach (DeviceInformation info in found)
            {
                string address = AddressOf(info);
                Guid container = info.Properties.TryGetValue(ContainerProperty, out object? c) && c is Guid g ? g : Guid.Empty;
                if (address.Length == 0 || container == Guid.Empty)
                {
                    steps.Add(StepOutcomes.NotAttempted("pair-list-device", "A paired device had no readable address or container, so it is not matched."));
                    continue;
                }

                devices.Add(new PairedDevice(info.Id, info.Name, address, container));
            }

            steps.Add(StepOutcomes.FromHResult("pair-list", 0, devices.Count.ToString(CultureInfo.InvariantCulture) + " paired device(s)."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // The raw HRESULT and the type are kept; the message can carry a device path and is not.
            steps.Add(StepOutcomes.FromHResult("pair-list", ex.HResult, ex.GetType().Name));
        }

        return new PairedListResult(devices, steps);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static string AddressOf(DeviceInformation info)
    {
        if (info.Properties.TryGetValue(AddressProperty, out object? value) && value is string text)
        {
            string hex = new(text.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length == 12)
            {
                return hex.ToUpperInvariant();
            }
        }

        // The id of a Bluetooth device ends with the remote address after the local radio's.
        MatchCollection matches = MacInId().Matches(info.Id);
        return matches.Count > 0 ? matches[^1].Value.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant() : "";
    }

    public async Task<PairingCallResult> UnpairAsync(string deviceId, CancellationToken ct)
    {
        if (!FixPlatform.HasDeviceEnumeration)
        {
            return new PairingCallResult("NotAvailable", -1, false, "Windows is older than 10.0.19041.");
        }

        return await UnpairGuardedAsync(deviceId, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private async Task<PairingCallResult> UnpairGuardedAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            CallsMade.Add("UnpairAsync");
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(ct).ConfigureAwait(false);
            DeviceUnpairingResult result = await info.Pairing.UnpairAsync().AsTask(ct).ConfigureAwait(false);
            DeviceUnpairingResultStatus status = result.Status;
            return new PairingCallResult(status.ToString(), (int)status, status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired, null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return new PairingCallResult(ex.GetType().Name, ex.HResult, false, "HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    public IUnpairedWatch Watch()
    {
        CallsMade.Add("CreateWatcher(unpaired selector)");
        return FixPlatform.HasDeviceEnumeration ? new RealWatch() : new NoWatch();
    }

    // Windows older than the one that has the API: a scan that sees nothing.
    private sealed class NoWatch : IUnpairedWatch
    {
        public async IAsyncEnumerable<WatchEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public void Dispose()
        {
        }
    }

    public async Task<PairingCallResult> PairAsync(string deviceId, IPairingPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!FixPlatform.HasDeviceEnumeration)
        {
            return new PairingCallResult("NotAvailable", -1, false, "Windows is older than 10.0.19041.");
        }

        return await PairGuardedAsync(deviceId, prompt, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private async Task<PairingCallResult> PairGuardedAsync(string deviceId, IPairingPrompt prompt, CancellationToken ct)
    {
        try
        {
            CallsMade.Add("Custom.PairAsync");
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(ct).ConfigureAwait(false);
            DeviceInformationCustomPairing custom = info.Pairing.Custom;
            void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => _ = AnswerAsync(args, prompt);
            custom.PairingRequested += OnRequested;
            try
            {
                DevicePairingResult result = await custom.PairAsync(
                    DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin,
                    DevicePairingProtectionLevel.Default).AsTask(ct).ConfigureAwait(false);
                DevicePairingResultStatus status = result.Status;
                return new PairingCallResult(status.ToString(), (int)status, status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired, null);
            }
            finally
            {
                custom.PairingRequested -= OnRequested;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return new PairingCallResult(ex.GetType().Name, ex.HResult, false, "HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    // The request is answered after the person has seen it, so the event is held open with a deferral.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task AnswerAsync(DevicePairingRequestedEventArgs args, IPairingPrompt prompt)
    {
        using Windows.Foundation.Deferral deferral = args.GetDeferral();
        FixPairKind kind = args.PairingKind switch
        {
            DevicePairingKinds.ConfirmOnly => FixPairKind.ConfirmOnly,
            DevicePairingKinds.ConfirmPinMatch => FixPairKind.ConfirmPinMatch,
            DevicePairingKinds.DisplayPin => FixPairKind.DisplayPin,
            _ => FixPairKind.Other,
        };
        bool accept = await prompt.ConfirmAsync(kind, args.Pin).ConfigureAwait(false);
        if (accept)
        {
            args.Accept();
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private sealed class RealWatch : IUnpairedWatch
    {
        private readonly Channel<WatchEvent> _channel = Channel.CreateUnbounded<WatchEvent>();
        private DeviceWatcher? _watcher;

        public async IAsyncEnumerable<WatchEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (_watcher is null)
            {
                string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
                _watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties);
                _watcher.Added += (_, info) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Added, Describe(info.Id, info.Name, info.Properties)));
                _watcher.Updated += (_, update) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Updated, Describe(update.Id, "", update.Properties)));
                _watcher.Removed += (_, update) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Removed, new UnpairedDevice(update.Id, "", "", false)));
                _watcher.EnumerationCompleted += (_, _) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.InitialListDone, null));
                _watcher.Start();
            }

            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out WatchEvent? e))
                {
                    yield return e;
                }
            }
        }

        private static UnpairedDevice Describe(string id, string name, IReadOnlyDictionary<string, object> properties)
        {
            string address = "";
            if (properties.TryGetValue(AddressProperty, out object? a) && a is string text)
            {
                string hex = new(text.Where(Uri.IsHexDigit).ToArray());
                address = hex.Length == 12 ? hex.ToUpperInvariant() : "";
            }

            if (address.Length == 0)
            {
                MatchCollection matches = MacInId().Matches(id);
                address = matches.Count > 0 ? matches[^1].Value.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant() : "";
            }

            bool av = properties.TryGetValue(MajorClassProperty, out object? m) && m is uint major && major == AudioVideoMajorClass;
            return new UnpairedDevice(id, name, address, av);
        }

        public void Dispose()
        {
            DeviceWatcher? watcher = _watcher;
            _watcher = null;
            if (watcher is not null && watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }

            _channel.Writer.TryComplete();
        }
    }
}

// ---- the re-pair ----

internal sealed record RePairTarget(string Address, string Name);

internal enum RePairFailure
{
    None,
    NotResolved,
    NotAirPods,
    UnpairFailed,
    NoDevice,
    Cancelled,
    PairFailed,
    NotAirPodsAfterPair,
}

// Touched: something was removed or a scan began, so the normal block must follow.
internal sealed record RePairResult(
    bool Paired,
    RePairFailure Failure,
    AirPodsCandidate? Adoptable,
    IReadOnlyList<PairingCallResult> Statuses,
    IReadOnlyList<string> Lines,
    bool Touched,
    bool NodesRemainedAfterUnpair,
    IReadOnlyList<StepOutcome> Steps);

internal sealed class RePairer
{
    private readonly IPairingApi _api;
    private readonly INodeReader _nodes;
    private readonly IFixRePairUi _ui;
    private readonly FixOptions _options;
    private readonly ILog _log;

    public RePairer(IPairingApi api, INodeReader nodes, IFixRePairUi ui, FixOptions options, ILog log)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _api = api;
        _nodes = nodes;
        _ui = ui;
        _options = options;
        _log = log;
    }

    // Apple's words for putting AirPods in pairing mode.
    // https://support.apple.com/en-us/108786
    public const string PairingModeInstruction =
        "Open the case lid. For AirPods Pro 3, double-tap the front of the case until the status light flashes white. " +
        "For older AirPods, press and hold the setup button on the back of the case until the status light flashes white.";

    public async Task<RePairResult> RunAsync(FixConsent consent, RePairTarget? target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (consent.Kind != FixConsentKind.RePair)
        {
            throw new ArgumentException("Removing and pairing again needs the consent of the second question.", nameof(consent));
        }

        var lines = new List<string>();
        var statuses = new List<PairingCallResult>();
        var steps = new List<StepOutcome>();
        bool touched = false;
        bool remained = false;

        if (target is not null)
        {
            PairedListResult list = await _api.ListPairedAsync(ct).ConfigureAwait(false);
            steps.AddRange(list.Steps);
            List<PairedDevice> matches = list.Devices.Where(d => string.Equals(d.Address12, target.Address, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
            {
                lines.Add("Windows does not list exactly one paired device for " + target.Name + " (" + matches.Count.ToString(CultureInfo.InvariantCulture) + "), so nothing was removed.");
                return Done(false, RePairFailure.NotResolved);
            }

            // Checked again now, not at the time of the plan: the device must still be AirPods by the identity rule.
            PairedDevice paired = matches[0];
            AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
            steps.AddRange(found.Steps.Where(s => !s.Ok));
            if (!found.Listed || !found.Candidates.Any(c => c.Address == paired.Address12 && c.ContainerId == paired.ContainerId))
            {
                lines.Add(target.Name + " is not AirPods by Earshot's rule on this PC now, so it was not removed.");
                return Done(false, RePairFailure.NotAirPods);
            }

            touched = true;
            PairingCallResult unpair = await _api.UnpairAsync(paired.Id, ct).ConfigureAwait(false);
            statuses.Add(unpair);
            lines.Add("Remove " + target.Name + ": " + unpair.Name + " (" + unpair.Code.ToString(CultureInfo.InvariantCulture) + ").");
            if (!unpair.Success)
            {
                lines.Add("Windows did not remove it. Nothing else was done.");
                return Done(false, RePairFailure.UnpairFailed);
            }

            AirPodsDiscoveryResult after = AirPodsDiscovery.Find(_nodes);
            remained = after.Listed && after.Candidates.Any(c => c.Address == target.Address);
            lines.Add(remained
                ? "Windows' device list still holds entries for it after the removal. Whether the removal clears them as Settings' Remove device does is unproved."
                : "The device entries for it are gone from Windows' list.");
        }

        // The scan. It starts only here, after the second OK.
        touched = true;
        using IUnpairedWatch watch = _api.Watch();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seen = new List<WatchEvent>();
        var baselineDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pump = PumpAsync(watch, seen, baselineDone, pumpCts.Token);

        try
        {
            using (var baselineWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // A real wait, not the injected one: the list Windows already holds is read for as long as it takes, up to this.
                Task limit = Task.Delay(_options.BaselineWait, baselineWait.Token);
                await Task.WhenAny(baselineDone.Task, limit).ConfigureAwait(false);
                await baselineWait.CancelAsync().ConfigureAwait(false);
            }

            HashSet<string> baseline;
            int mark;
            lock (seen)
            {
                // Everything up to the end of the first listing is what Windows already knew. Events that came in after that
                // point, even before this line ran, are not part of it.
                int listed = seen.FindIndex(e => e.Kind == WatchEventKind.InitialListDone);
                mark = listed >= 0 ? listed + 1 : seen.Count;
                baseline = seen.Take(mark).Where(e => e.Device is not null).Select(e => e.Device!.Id).ToHashSet(StringComparer.Ordinal);
            }

            _ui.ShowInstruction(PairingModeInstruction);

            List<UnpairedDevice> candidates = await WaitForCandidatesAsync(seen, mark, baseline, pump, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                lines.Add("No new Bluetooth audio device appeared. Nothing was paired.");
                return Done(false, RePairFailure.NoDevice);
            }

            UnpairedDevice? chosen = await _ui.PickAsync(candidates).ConfigureAwait(false);
            if (chosen is null)
            {
                lines.Add("No device was chosen. Nothing was paired.");
                return Done(false, RePairFailure.Cancelled);
            }

            // The scan has done its job; stop it before pairing starts.
            await pumpCts.CancelAsync().ConfigureAwait(false);
            PairingCallResult pair = await _api.PairAsync(chosen.Id, new Prompt(_ui), ct).ConfigureAwait(false);
            statuses.Add(pair);
            lines.Add("Pair " + chosen.Name + ": " + pair.Name + " (" + pair.Code.ToString(CultureInfo.InvariantCulture) + ")" + (pair.Detail is null ? "." : ", " + pair.Detail + "."));
            if (!pair.Success)
            {
                lines.Add("Windows did not pair it. If pairing from Earshot does not work on this PC, pair the AirPods in Windows Settings (Bluetooth and devices, Add device), then run Fix connection again.");
                return Done(false, RePairFailure.PairFailed);
            }

            PairedListResult now = await _api.ListPairedAsync(ct).ConfigureAwait(false);
            steps.AddRange(now.Steps);
            PairedDevice? newPaired = now.Devices.Count(d => d.Address12 == chosen.Address12) == 1 && chosen.Address12.Length == 12
                ? now.Devices.Single(d => d.Address12 == chosen.Address12)
                : null;
            AirPodsCandidate? match = null;
            if (newPaired is not null)
            {
                for (TimeSpan waited = TimeSpan.Zero; ; waited += TimeSpan.FromSeconds(1))
                {
                    AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
                    steps.AddRange(found.Steps.Where(s => !s.Ok));
                    match = found.Candidates.FirstOrDefault(c => c.Address == newPaired.Address12 && c.ContainerId == newPaired.ContainerId);
                    if (match is not null || waited >= _options.PostPairWait)
                    {
                        break;
                    }

                    await _options.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }

            if (match is null)
            {
                lines.Add(chosen.Name + " is paired but is not AirPods by Earshot's rule. It stays paired. Nothing was adopted.");
                return Done(true, RePairFailure.NotAirPodsAfterPair);
            }

            lines.Add(chosen.Name + " is paired and is AirPods by Earshot's rule.");
            return Done(true, RePairFailure.None, match);
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            _ui.Close();
        }

        RePairResult Done(bool paired, RePairFailure failure, AirPodsCandidate? adoptable = null) =>
            new(paired, failure, adoptable, statuses, lines, touched, remained, steps);
    }

    // Reads the watcher into the list until it ends or the scan is cancelled. The end of a scan by cancellation is how the scan
    // is meant to stop, and is recorded in the log; any other failure is not swallowed.
    private async Task PumpAsync(IUnpairedWatch watch, List<WatchEvent> seen, TaskCompletionSource baselineDone, CancellationToken ct)
    {
        try
        {
            await foreach (WatchEvent e in watch.EventsAsync(ct).ConfigureAwait(false))
            {
                lock (seen)
                {
                    seen.Add(e);
                }

                if (e.Kind == WatchEventKind.InitialListDone)
                {
                    baselineDone.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info("fix: the scan for unpaired devices was stopped.");
        }
        finally
        {
            baselineDone.TrySetResult();
        }
    }

    private async Task<List<UnpairedDevice>> WaitForCandidatesAsync(List<WatchEvent> seen, int mark, HashSet<string> baseline, Task pump, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ScanTimeout);
        bool settling = false;
        while (true)
        {
            List<UnpairedDevice> found = CandidatesNow(seen, mark, baseline);
            if (found.Count > 0 && !settling)
            {
                settling = true;
                timeout.CancelAfter(_options.SettleWait);
            }

            if (pump.IsCompleted || timeout.IsCancellationRequested)
            {
                // One last look: events that arrived while the wait ended.
                return CandidatesNow(seen, mark, baseline);
            }

            try
            {
                await Task.WhenAny(pump, _options.Delay(TimeSpan.FromMilliseconds(10), timeout.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    // A classic device of the Audio/Video major class that first appeared after the prompt was shown. A device already in the
    // list before the prompt is never a candidate: it is whatever Windows already knew about.
    private static List<UnpairedDevice> CandidatesNow(List<WatchEvent> seen, int mark, HashSet<string> baseline)
    {
        lock (seen)
        {
            return seen.Skip(mark)
                .Where(e => e.Kind is WatchEventKind.Added or WatchEventKind.Updated && e.Device is { IsAudioVideo: true } d && !baseline.Contains(d.Id))
                .Select(e => e.Device!)
                .DistinctBy(d => d.Id)
                .ToList();
        }
    }

    private sealed class Prompt(IFixRePairUi ui) : IPairingPrompt
    {
        public Task<bool> ConfirmAsync(FixPairKind kind, string? pin) => kind switch
        {
            // The second question already said Windows will pair the device; a bare confirmation needs no more.
            FixPairKind.ConfirmOnly => Task.FromResult(true),
            FixPairKind.ConfirmPinMatch or FixPairKind.DisplayPin => ui.ConfirmPinAsync(kind, pin),
            _ => Task.FromResult(false),
        };
    }
}
