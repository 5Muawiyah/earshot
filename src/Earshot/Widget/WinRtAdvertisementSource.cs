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
    public StepOutcome Start()
    {
        if (!WidgetPlatformGuard.HasBleWatcher)
        {
            return StepOutcomes.NotAvailable(StartStep, "This build of Windows has no BLE advertisement watcher.");
        }

        lock (_gate)
        {
            try
            {
                if (_watcher is null)
                {
                    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                    watcher.Received += OnReceived;
                    watcher.Stopped += OnStopped;
                    _watcher = watcher;
                }

                _watcher.Start();
                return StepOutcomes.FromHResult(StartStep, 0, detail: "status " + _watcher.Status);
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
            if (_watcher is { } watcher)
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
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

    // Only ever subscribed inside Start(); see OnReceived's comment.
    [SupportedOSPlatform("windows10.0.19041.0")]
    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        BluetoothError error = args.Error;
        StepOutcome step = StepOutcomes.FromWin32(StopStep + ":event", (uint)error, ok: error == BluetoothError.Success);
        Stopped?.Invoke(this, new AdvertisementSourceStopped((int)error, error.ToString(), step));
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
