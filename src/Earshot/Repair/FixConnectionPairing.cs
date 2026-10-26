using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Pairing;
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

// A device the scan listed. MinorClass and IsPresent are null until a watcher event reports them; the Bluetooth class of device
// is major then minor (Audio/Video, then Headphones or Wearable headset for AirPods).
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothminorclass
internal sealed record UnpairedDevice(string Id, string Name, string Address12, bool IsAudioVideo, uint? MinorClass = null, bool? IsPresent = null);

internal enum WatchEventKind
{
    Added,
    Updated,
    Removed,
    InitialListDone,

    // Windows stopped the watcher on its own (DeviceWatcherStatus.Aborted): the scan sees nothing more.
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcherstatus
    Aborted,
}

internal sealed record WatchEvent(WatchEventKind Kind, UnpairedDevice? Device);

internal sealed record PairedListResult(IReadOnlyList<PairedDevice> Devices, IReadOnlyList<StepOutcome> Steps);

// A status Windows returned: the enum name, its number, and whether it means done. Threw says the call raised an exception
// instead: Name is then the exception type and Code its HRESULT, not a status.
internal sealed record PairingCallResult(string Name, int Code, bool Success, string? Detail, bool Threw = false);

// The first failure of the pairing request's answer, from the request event, whose task nobody awaits. A pairing whose handler
// failed is that, with the type and HRESULT, not the RejectedByHandler Windows then reports, which would read as a cancel.
internal sealed class PairingHandlerFailure
{
    internal const string Name = "handler-failed";

    private Exception? _first;

    public void Record(Exception? failure)
    {
        if (failure is not null)
        {
            _ = Interlocked.CompareExchange(ref _first, failure, null);
        }
    }

    public PairingCallResult Apply(PairingCallResult windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Exception? failure = Volatile.Read(ref _first);
        return failure is null
            ? windows
            : new PairingCallResult(Name, failure.HResult, false, failure.GetType().Name + ", HRESULT 0x" + failure.HResult.ToString("X8", CultureInfo.InvariantCulture) + "; Windows answered " + windows.Name, Threw: true);
    }
}

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
    internal const string MinorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Minor";
    internal const string IsPresentProperty = "System.Devices.Aep.IsPresent";
    internal const string NameProperty = "System.ItemNameDisplay";

    // Class of device, major class: Audio/Video.
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothmajorclass
    private const uint AudioVideoMajorClass = 4;

    private static readonly string[] RequestedProperties = [AddressProperty, ContainerProperty, MajorClassProperty, MinorClassProperty, IsPresentProperty, NameProperty];

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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            return new PairingCallResult("NotAvailable", -1, false, "Windows is older than 10.0.19041.", Threw: true);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PairingCallResult(ex.GetType().Name, ex.HResult, false, "HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture), Threw: true);
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
            return new PairingCallResult("NotAvailable", -1, false, "Windows is older than 10.0.19041.", Threw: true);
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
            var failure = new PairingHandlerFailure();
            void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args) => _ = AnswerAsync(args, prompt, failure);
            custom.PairingRequested += OnRequested;
            try
            {
                DevicePairingResult result = await custom.PairAsync(
                    DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin,
                    DevicePairingProtectionLevel.Default).AsTask(ct).ConfigureAwait(false);
                DevicePairingResultStatus status = result.Status;
                return failure.Apply(new PairingCallResult(status.ToString(), (int)status, status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired, null));
            }
            finally
            {
                custom.PairingRequested -= OnRequested;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PairingCallResult(ex.GetType().Name, ex.HResult, false, "HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture), Threw: true);
        }
    }

    // The request is answered after the person has seen it, so the event is held open with a deferral. Nothing awaits this
    // task, so a failure is kept in failure, never thrown.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task AnswerAsync(DevicePairingRequestedEventArgs args, IPairingPrompt prompt, PairingHandlerFailure failure)
    {
        try
        {
            using Windows.Foundation.Deferral deferral = args.GetDeferral();
            FixPairKind kind = args.PairingKind switch
            {
                DevicePairingKinds.ConfirmOnly => FixPairKind.ConfirmOnly,
                DevicePairingKinds.ConfirmPinMatch => FixPairKind.ConfirmPinMatch,
                DevicePairingKinds.DisplayPin => FixPairKind.DisplayPin,
                _ => FixPairKind.Other,
            };
            failure.Record(await AnswerRequestAsync(kind, args.Pin, prompt, args.Accept).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // GetDeferral, PairingKind or Pin.
            failure.Record(ex);
        }
    }

    // The question and the accept, apart from the WinRT event. A failure of either is returned, with nothing accepted after it.
    internal static async Task<Exception?> AnswerRequestAsync(FixPairKind kind, string? pin, IPairingPrompt prompt, Action accept)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(accept);
        try
        {
            if (await prompt.ConfirmAsync(kind, pin).ConfigureAwait(false))
            {
                accept();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // The attempt ended while the question was up: nothing is accepted, and Windows' own answer says how it ended.
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // What one event carries. A property the event lacks stays null (or false for the class), and the session merges the
    // events of one device, so a later update that carries only the name or the class fills in what was missing.
    internal static UnpairedDevice DescribeDevice(string id, string name, IReadOnlyDictionary<string, object> properties)
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

        if (name.Length == 0 && properties.TryGetValue(NameProperty, out object? shown) && shown is string display)
        {
            name = display;
        }

        bool av = properties.TryGetValue(MajorClassProperty, out object? m) && AsUInt(m) == AudioVideoMajorClass;
        uint? minor = properties.TryGetValue(MinorClassProperty, out object? n) ? AsUInt(n) : null;
        bool? present = properties.TryGetValue(IsPresentProperty, out object? p) && p is bool b ? b : null;
        return new UnpairedDevice(id, name, address, av, minor, present);
    }

    // The numeric types a property bag may hand back for a class of device field.
    private static uint? AsUInt(object? value) => value switch
    {
        uint u => u,
        int i and >= 0 => (uint)i,
        ushort us => us,
        short sh and >= 0 => (uint)sh,
        byte by => by,
        _ => null,
    };

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
                _watcher.Added += (_, info) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Added, DescribeDevice(info.Id, info.Name, info.Properties)));
                _watcher.Updated += (_, update) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Updated, DescribeDevice(update.Id, "", update.Properties)));
                _watcher.Removed += (_, update) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Removed, new UnpairedDevice(update.Id, "", "", false)));
                _watcher.EnumerationCompleted += (_, _) => _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.InitialListDone, null));
                _watcher.Stopped += (sender, _) =>
                {
                    if (sender.Status == DeviceWatcherStatus.Aborted)
                    {
                        _channel.Writer.TryWrite(new WatchEvent(WatchEventKind.Aborted, null));
                    }

                    _channel.Writer.TryComplete();
                };
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
    RadioNotReady,

    // Paired, and the device could not be looked at, so nothing was adopted.
    UnverifiedAfterPair,
}

// Touched: something was removed or a scan began, so the normal block must follow. Session is the pairing session's result when
// one ran (or was refused by the radio check), for the one connect-history entry of the run.
internal sealed record RePairResult(
    bool Paired,
    RePairFailure Failure,
    AirPodsCandidate? Adoptable,
    IReadOnlyList<PairingCallResult> Statuses,
    IReadOnlyList<string> Lines,
    bool Touched,
    bool NodesRemainedAfterUnpair,
    IReadOnlyList<StepOutcome> Steps,
    PairingSessionResult? Session = null);

// Fix connection's second step: remove the pinned pair, then pair again through the shared pairing session.
internal sealed class RePairer
{
    private readonly IPairingApi _api;
    private readonly INodeReader _nodes;
    private readonly IFixRePairUi _ui;
    private readonly FixOptions _options;
    private readonly ILog _log;
    private readonly IRadioCheck? _radio;

    public RePairer(IPairingApi api, INodeReader nodes, IFixRePairUi ui, FixOptions options, ILog log, IRadioCheck? radio = null)
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
        _radio = radio;
    }

    // Set once the run removes something or starts the scan, so a caller whose run was cut off (cancelled or thrown out of)
    // knows a pairing began. Read only once RunAsync has ended.
    internal bool Began { get; private set; }

    // The history steps of the pairing session so far, for that cut-off run's entry.
    internal IReadOnlyList<StepOutcome> SessionStepsSoFar => _session?.StepsSoFar ?? [];

    // The history steps this run took before the session: the removal of the old pair, with its status and time. Read only once
    // RunAsync has ended.
    internal IReadOnlyList<StepOutcome> StepsBeforeTheSession => [.. _before];

    private readonly List<StepOutcome> _before = [];

    private PairingSession? _session;

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
        var session = new PairingSession(_api, _nodes, _ui, _options, _log, _radio);
        _session = session;

        // The radio is read before anything is removed: a pair is never taken away from a PC whose Bluetooth cannot pair it again.
        RadioCheckResult? radio = await session.CheckRadioAsync(ct).ConfigureAwait(false);
        if (radio is { BlocksRepair: true })
        {
            PairingSessionResult refused = await session.RunAsync(excludeAlreadyListed: true, ct, radio).ConfigureAwait(false);
            lines.AddRange(refused.Lines);
            steps.AddRange(refused.Steps);
            return Done(false, RePairFailure.RadioNotReady, null, refused);
        }

        if (target is not null)
        {
            // Both calls before the window have budgets of their own: the person has no Cancel yet, and the run holds the Fix lock.
            PairedListResult list;
            using (var listWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                listWait.CancelAfter(_options.PairedListWait);
                try
                {
                    list = await _api.ListPairedAsync(listWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    steps.Add(StepOutcomes.EarshotTimeout("pair-list", "The paired list did not answer within " + _options.PairedListWait.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms."));
                    lines.Add("Windows did not list the paired devices in time, so nothing was removed.");
                    return Done(false, RePairFailure.NotResolved);
                }
            }

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
            Began = true;
            PairingCallResult unpair;
            bool unanswered = false;
            var unpairWatch = System.Diagnostics.Stopwatch.StartNew();
            using (var unpairWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                unpairWait.CancelAfter(_options.UnpairWait);
                try
                {
                    unpair = await _api.UnpairAsync(paired.Id, unpairWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    unanswered = true;
                    unpair = new PairingCallResult(StepOutcomes.EarshotTimeoutName, StepOutcomes.EarshotTimeoutCode, false, null);
                }
            }

            statuses.Add(unpair);
            _before.Add(new StepOutcome("unpair", unpair.Success, unpair.Code, unpair.Name, null));
            _before.Add(new StepOutcome("unpair-ms", true, (int)Math.Min(unpairWatch.ElapsedMilliseconds, int.MaxValue), "ms", null));
            lines.Add("Remove " + target.Name + ": " + unpair.Name + " (" + unpair.Code.ToString(CultureInfo.InvariantCulture) + ").");
            if (unanswered)
            {
                lines.Add("Windows did not answer in time, so it is not known whether it was removed. Nothing else was done.");
                return Done(false, RePairFailure.UnpairFailed);
            }

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

        // The scan. It starts only here, after the second OK. The removed pair is still listed by the watcher, so what was listed
        // before the instruction is never offered back.
        touched = true;
        Began = true;
        PairingSessionResult ran = await session.RunAsync(excludeAlreadyListed: true, ct, radio).ConfigureAwait(false);
        lines.AddRange(ran.Lines);
        steps.AddRange(ran.Steps);
        if (ran.PairStatus is not null)
        {
            statuses.Add(ran.PairStatus);
        }

        RePairFailure failure = ran.Reason switch
        {
            PairFailureReason.None => RePairFailure.None,
            PairFailureReason.NoDevice => RePairFailure.NoDevice,
            PairFailureReason.Cancelled => RePairFailure.Cancelled,
            PairFailureReason.NotAirPods => RePairFailure.NotAirPodsAfterPair,
            PairFailureReason.Unverified => RePairFailure.UnverifiedAfterPair,
            PairFailureReason.RadioNotReady => RePairFailure.RadioNotReady,
            _ => RePairFailure.PairFailed,
        };
        return Done(ran.Paired, failure, ran.Adoptable, ran);

        RePairResult Done(bool paired, RePairFailure fail, AirPodsCandidate? adoptable = null, PairingSessionResult? run = null) =>
            new(paired, fail, adoptable, statuses, lines, touched, remained, steps, run);
    }
}
