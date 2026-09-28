using System.Runtime.Versioning;
using System.Security.Cryptography;
using Earshot.Contracts;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Earshot.Widget;

// The real IAdvertisementSource: BluetoothLEAdvertisementWatcher, passive, unfiltered. It never connects,
// pairs, scans actively or touches a device node; it only listens.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher
internal sealed class WinRtAdvertisementSource : IAdvertisementSource
{
    private const string StartStep = "watcher-start";
    private const string StopStep = "watcher-stop";

    // A key made for this run and never stored: the tag it produces for a sender identifies the same
    // address across one run's advertisements without ever writing the address itself anywhere.
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Lock _gate = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private int _disposed;

    // The caller's label for whichever run is currently live, in a mutable box rather
    // than a plain field, so a Start() call that finds the native watcher already running (BluetoothLEAdvertisementWatcher's
    // own Status can still read Started for a moment after Stop() was called, so a Suspend immediately
    // followed by a Resume can land here) can retag the box in place with the caller's newest value without
    // touching whichever earlier watcher's own closure still holds an older box. A fresh native watcher gets
    // its own fresh box, so an old, already-superseded watcher's late Stopped keeps echoing whatever
    // generation it was last actually, freshly, given - never the current box's now-later value.
    // volatile: written under _gate by Start(), read by the Stopped closure on whatever thread Windows calls
    // it back on, with no lock of its own there.
    private sealed class GenerationBox
    {
        internal volatile int Value;
    }

    private GenerationBox? _generationBox;

    // Test seam only: counts real constructions so WidgetRealSurfaceGuardTests can prove a test harness
    // never builds this class in place of a fake. Never read or reset in production.
    internal static int ConstructionCount;

    public WinRtAdvertisementSource()
    {
        Interlocked.Increment(ref ConstructionCount);
    }

    public event EventHandler<AdvertisementSample>? Received;

    public event EventHandler<AdvertisementSourceStopped>? Stopped;

    public AdvertisementSourceState State
    {
        get
        {
            if (!WidgetPlatformGuard.HasBleWatcher)
            {
                return AdvertisementSourceState.Created;
            }

            lock (_gate)
            {
                return _watcher is null ? AdvertisementSourceState.Created : StatusOf(_watcher.Status);
            }
        }
    }

    // Read-only: proves the real watcher is Passive, rather than trusting the constant Start() sets.
    // Null before Start() has constructed the watcher, or when this build has no BLE watcher to check.
    public BluetoothLEScanningMode? ScanningMode
    {
        get
        {
            if (!WidgetPlatformGuard.HasBleWatcher)
            {
                return null;
            }

            lock (_gate)
            {
                return _watcher?.ScanningMode;
            }
        }
    }

    // Passive is the documented default and sends no scan request packets; Active does. No
    // AdvertisementFilter and no SignalStrengthFilter: the positive control needs every advertisement
    // counted, and a signal filter would turn the -127 out-of-range sentinel on. AllowExtendedAdvertisements
    // stays false.
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothlescanningmode
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementreceivedeventargs
    public StepOutcome Start(int generation)
    {
        if (!WidgetPlatformGuard.HasBleWatcher)
        {
            return StepOutcomes.NotAvailable(StartStep, "This build of Windows has no BLE advertisement watcher.");
        }

        lock (_gate)
        {
            if (_disposed != 0)
            {
                // A delayed retry that was already past its early checks when
                // Dispose ran must not resurrect a native watcher behind this wrapper's back once this call
                // finally reaches the front of whatever queued it. Refuse, with a step the caller logs,
                // rather than silently doing nothing or starting a watcher nobody will ever stop again.
                return StepOutcomes.NotAttempted(StartStep, "Disposed.");
            }

            try
            {
                if (_watcher is { } running && StatusOf(running.Status) == AdvertisementSourceState.Started)
                {
                    // Already running - a redundant Start() (a manual refresh racing
                    // the retry timer, or a Resume that lands before BluetoothLEAdvertisementWatcher's own
                    // Status has settled back to Stopped after Suspend's Stop(), for instance) leaves the
                    // current native watcher exactly as it is, but the caller's idea of the generation still
                    // moved on; retag the box in place so this run's eventual Stopped echoes the caller's
                    // newest value, not the one it was last freshly given.
                    if (_generationBox is { } runningBox)
                    {
                        runningBox.Value = generation;
                    }

                    return StepOutcomes.FromHResult(StartStep, 0, detail: "status " + running.Status);
                }

                // A fresh native watcher per Start(), not the one instance reused across Stop()/Start()
                // cycles: see AdvertisementSourceStopped's comment. The old watcher's Received is dropped so
                // it cannot double-report once superseded; its own eventual Stopped, if any, still arrives
                // through the closure captured below and is reported with the generation box it belongs to,
                // which this fresh Start() never touches.
                if (_watcher is { } previous)
                {
                    previous.Received -= OnReceived;
                }

                var generationBox = new GenerationBox { Value = generation };
                _generationBox = generationBox;
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                watcher.Received += OnReceived;

                // A lambda, not a plain method-group subscription like OnReceived's above, because each one
                // needs its own generation box baked in. The guard is repeated here, even though this line
                // only ever runs already past the same check above: the call inside a lambda's own compiled
                // body is a call site the platform-compatibility analyser considers separately from the
                // method that created it, since the delegate can in principle outlive that guard's scope.
                watcher.Stopped += (sender, args) =>
                {
                    if (WidgetPlatformGuard.HasBleWatcher)
                    {
                        OnStopped(args, generationBox.Value);
                    }
                };
                _watcher = watcher;
                watcher.Start();
                return StepOutcomes.FromHResult(StartStep, 0, detail: "status " + watcher.Status);
            }
            catch (Exception ex)
            {
                // Not a silent catch: the raw code and the exception type are returned as the step.
                return StepOutcomes.FromHResult(StartStep, ex.HResult, detail: ex.GetType().Name, ok: false);
            }
        }
    }

    public StepOutcome Stop()
    {
        if (!WidgetPlatformGuard.HasBleWatcher)
        {
            return StepOutcomes.NotAvailable(StopStep, "This build of Windows has no BLE advertisement watcher.");
        }

        lock (_gate)
        {
            if (_watcher is null)
            {
                return StepOutcomes.NotAttempted(StopStep, "Never started.");
            }

            try
            {
                _watcher.Stop();
                return StepOutcomes.FromHResult(StopStep, 0, detail: "status " + _watcher.Status);
            }
            catch (Exception ex)
            {
                return StepOutcomes.FromHResult(StopStep, ex.HResult, detail: ex.GetType().Name, ok: false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        if (!WidgetPlatformGuard.HasBleWatcher)
        {
            return;
        }

        lock (_gate)
        {
            // Stopped is subscribed as a per-generation closure (see Start()), not this instance's own
            // method, so there is nothing to unsubscribe from it here: it fires once more, harmlessly, once
            // the watcher this Dispose just stopped actually finishes, and nothing is listening for the
            // public Stopped event any more by then anyway (the owner unsubscribes before disposing).
            if (_watcher is { } watcher)
            {
                watcher.Received -= OnReceived;
            }
        }
    }

    // Whatever thread Windows calls back on. One sample per manufacturer-data section, whatever the
    // company: the widget's own parser decides what a non-Apple section means (WrongCompany, counted, no
    // field read), so this only copies what Windows gave it. Only ever subscribed inside Start(), which
    // already checked WidgetPlatformGuard.HasBleWatcher, so the OS version this needs is already met by
    // the time Windows can call it.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        sbyte rssi = (sbyte)Math.Clamp((int)args.RawSignalStrengthInDBm, sbyte.MinValue, sbyte.MaxValue);
        DateTimeOffset timestamp = args.Timestamp;
        uint tag = SenderTagOf(args.BluetoothAddress);

        foreach (BluetoothLEManufacturerData section in args.Advertisement.ManufacturerData)
        {
            byte[] data = ReadBuffer(section.Data);
            Received?.Invoke(this, new AdvertisementSample(section.CompanyId, data, rssi, timestamp, tag));
        }
    }

    // Subscribed as a closure inside Start(), one per generation, so a late event always carries the
    // generation of the run it actually belongs to, whatever Start() calls have happened since; see
    // AdvertisementSourceStopped's own comment and Start()'s.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private void OnStopped(BluetoothLEAdvertisementWatcherStoppedEventArgs args, int generation)
    {
        BluetoothError error = args.Error;
        StepOutcome step = StepOutcomes.FromWin32(StopStep + ":event", (uint)error, ok: error == BluetoothError.Success);
        Stopped?.Invoke(this, new AdvertisementSourceStopped((int)error, error.ToString(), step, generation));
    }

    private uint SenderTagOf(ulong address)
    {
        byte[] addressBytes = BitConverter.GetBytes(address);
        byte[] hash = HMACSHA256.HashData(_key, addressBytes);
        return BitConverter.ToUInt32(hash, 0);
    }

    // Only ever called from OnReceived; see its comment.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var data = new byte[buffer.Length];
        using DataReader reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(data);
        return data;
    }

    // Status names for the log: Created 0, Started 1, Stopping 2, Stopped 3, Aborted 4. Stopping has no
    // separate value in AdvertisementSourceState, so it reads as Stopped: not yet delivering, which is what
    // matters to every caller of State.
    // https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcherstatus
    // Only ever called from Start(), Stop() or State, each already behind WidgetPlatformGuard.HasBleWatcher.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static AdvertisementSourceState StatusOf(BluetoothLEAdvertisementWatcherStatus status) => status switch
    {
        BluetoothLEAdvertisementWatcherStatus.Created => AdvertisementSourceState.Created,
        BluetoothLEAdvertisementWatcherStatus.Started => AdvertisementSourceState.Started,
        BluetoothLEAdvertisementWatcherStatus.Aborted => AdvertisementSourceState.Aborted,
        _ => AdvertisementSourceState.Stopped,
    };
}
