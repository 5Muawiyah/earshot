using System.Runtime.Versioning;
using Earshot.Contracts;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Earshot.Streaming;

// The only file that calls WinRT. "Represents a connection that allows a remote device to stream audio to a
// Windows device." Introduced in 10.0.19041.0, agile, and usable from any thread (MarshalingType.Agile,
// ThreadingModel.Both), so nothing here needs or asks for a UI thread.
// https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection
//
// What it never does: pair anything (a desktop app cannot: DeviceInformationPairing.PairAsync is listed as
// unsupported), scan for devices, change the default playback device, ask for elevation, or name a device
// interface or a selector of its own. The selector always comes from GetDeviceSelector. On the owner's PC it
// read as a query over enabled device interfaces of paired devices that offer the A2DP source service, which
// is a read of the device store and sends nothing over the radio; the binding test checks it is still not an
// association endpoint query, the kind that scans.
// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support
//
// Only the asynchronous forms are used. Start and Open exist, but they talk to a radio and no page gives them
// a time limit. Every await is ConfigureAwait(false); AsTask(CancellationToken) comes from the projection.
// https://learn.microsoft.com/en-us/dotnet/api/system.windowsruntimesystemextensions
//
// Every call is behind PlatformGuard, and every catch records the raw HRESULT and the exception type on the
// record it returns. The exception message is never kept: text from Windows can carry a device path.
// A connection the platform holds, as far as letting go of it is concerned. The real one wraps an
// AudioPlaybackConnection; a test stands one in, so what Release does when Windows will not close a connection can
// be run without one.
internal interface IHeldLink
{
    // Releases the connection. Never throws: a failure comes back as a step with the raw HRESULT and the type.
    StepOutcome Close();
}

internal sealed class WindowsStreamingPlatform : IStreamingPlatform
{
    // Asked for so the device Earshot manages can be recognised by its container, without reading the id.
    // https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties
    internal const string ContainerIdProperty = "System.Devices.ContainerId";

    private const string SupportStep = "streaming-support";
    private const string ListStep = "streaming-list";
    private const string CreateStep = "streaming-create";
    private const string EnableStep = "streaming-enable";
    private const string OpenStep = "streaming-open";
    private const string ReleaseStep = "streaming-release";

    private static readonly string[] RequestedProperties = [ContainerIdProperty];

    private readonly ILog _log;
    private readonly Lock _gate = new();

    // A connection stays here for as long as it should stay enabled: "the underlying transport is deactivated
    // when all references are released", so one the collector could reach would stop by itself. It also stays
    // here when Windows would not close it, so the next Release tries the same connection again.
    private readonly Dictionary<string, IHeldLink> _links = new(StringComparer.Ordinal);

    // Ids whose connection is being created and enabled right now, so a second call for the same id never makes a
    // second connection that would then have to be let go of quietly.
    private readonly HashSet<string> _enabling = new(StringComparer.Ordinal);

    public WindowsStreamingPlatform(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public event EventHandler<StreamingLinkChanged>? LinkChanged;

    public StreamingSupportCheck CheckSupport()
    {
        if (!PlatformGuard.HasAudioPlaybackConnection)
        {
            return new StreamingSupportCheck(StreamingSupport.BuildTooOld, StepOutcomes.NotAvailable(SupportStep, "Windows is older than 10.0.19041."));
        }

        try
        {
            _ = AudioPlaybackConnection.GetDeviceSelector();
            return new StreamingSupportCheck(StreamingSupport.Supported, StepOutcomes.FromHResult(SupportStep, 0));
        }
        catch (Exception ex)
        {
            return new StreamingSupportCheck(StreamingSupport.TypeMissing, StreamingCoordinator.Failure(SupportStep, ex));
        }
    }

    public async Task<StreamingDiscovery> ListStreamCapableDevicesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.HasAudioPlaybackConnection)
        {
            return new StreamingDiscovery(StreamingDiscoveryStatus.Unsupported, [], StepOutcomes.NotAvailable(ListStep, StreamingDetail.Support));
        }

        try
        {
            // The string overload, with the selector Windows supplies. One read that ends, not a watcher left running.
            // https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformation.findallasync
            string selector = AudioPlaybackConnection.GetDeviceSelector();
            DeviceInformationCollection found = await DeviceInformation.FindAllAsync(selector, RequestedProperties)
                .AsTask(cancellationToken).ConfigureAwait(false);

            var devices = new List<StreamingDevice>(found.Count);
            foreach (DeviceInformation information in found)
            {
                devices.Add(new StreamingDevice(information.Id, information.Name ?? "", ContainerOf(information)));
            }

            return new StreamingDiscovery(StreamingDiscoveryStatus.Ok, devices, StepOutcomes.FromHResult(ListStep, 0));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StreamingDiscovery(StreamingDiscoveryStatus.Failed, [], StreamingCoordinator.Failure(ListStep, ex));
        }
    }

    public async Task<StreamingEnableOutcome> EnableAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (!PlatformGuard.HasAudioPlaybackConnection)
        {
            return new StreamingEnableOutcome(StreamingEnableStatus.Unsupported, deviceId, StepOutcomes.NotAvailable(EnableStep, StreamingDetail.Support));
        }

        lock (_gate)
        {
            if (_links.ContainsKey(deviceId))
            {
                return new StreamingEnableOutcome(StreamingEnableStatus.Enabled, deviceId, StepOutcomes.FromHResult(EnableStep, 0, "already enabled"));
            }

            if (!_enabling.Add(deviceId))
            {
                return new StreamingEnableOutcome(StreamingEnableStatus.StartFailed, deviceId, StepOutcomes.NotAttempted(EnableStep, StreamingDetail.EnableInFlight));
            }
        }

        try
        {
            AudioPlaybackConnection? connection;
            try
            {
                // "If the specified device does not have support for audio streaming, the return value is null."
                // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection.trycreatefromid
                connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
            }
            catch (Exception ex)
            {
                return new StreamingEnableOutcome(StreamingEnableStatus.StartFailed, deviceId, StreamingCoordinator.Failure(CreateStep, ex));
            }

            if (connection is null)
            {
                return new StreamingEnableOutcome(StreamingEnableStatus.NotStreamCapable, deviceId, StepOutcomes.FromHResult(CreateStep, 0, "no connection for this device"));
            }

            // Held from the moment it exists. If anything after this fails or is cancelled it is still held, and the
            // caller lets go of it through Release, where the outcome of that is recorded (see IStreamingPlatform).
            var link = new Link(this, deviceId, connection);
            lock (_gate)
            {
                _links[deviceId] = link;
            }

            try
            {
                link.Listen();

                // Enabling is its own step: "audio does not play until the connection has been opened".
                // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection.startasync
                await connection.StartAsync().AsTask(cancellationToken).ConfigureAwait(false);
                return new StreamingEnableOutcome(StreamingEnableStatus.Enabled, deviceId, StepOutcomes.FromHResult(EnableStep, 0));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new StreamingEnableOutcome(StreamingEnableStatus.StartFailed, deviceId, StreamingCoordinator.Failure(EnableStep, ex));
            }
        }
        finally
        {
            lock (_gate)
            {
                _enabling.Remove(deviceId);
            }
        }
    }

    public async Task<StreamingOpenOutcome> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (!PlatformGuard.HasAudioPlaybackConnection)
        {
            return new StreamingOpenOutcome(StreamingOpenStatus.Unsupported, deviceId, StepOutcomes.NotAvailable(OpenStep, StreamingDetail.Support));
        }

        Link? link;
        lock (_gate)
        {
            link = _links.TryGetValue(deviceId, out IHeldLink? held) ? held as Link : null;
        }

        if (link is null)
        {
            return new StreamingOpenOutcome(StreamingOpenStatus.NotEnabled, deviceId, StepOutcomes.NotAttempted(OpenStep, StreamingDetail.NotEnabled));
        }

        try
        {
            // The answer is an object, not a flag: Status says how it went and ExtendedError carries the code.
            // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionopenresult
            AudioPlaybackConnectionOpenResult result = await link.Connection.OpenAsync().AsTask(cancellationToken).ConfigureAwait(false);
            StreamingOpenStatus status = StreamingStatusMap.FromWinRt(result.Status);
            if (status == StreamingOpenStatus.Open)
            {
                return new StreamingOpenOutcome(status, deviceId, StepOutcomes.FromHResult(OpenStep, 0));
            }

            // The code is whatever Windows put in ExtendedError, and nothing at all when it put nothing there.
            int code = result.ExtendedError?.HResult ?? 0;
            StepOutcome step = code == 0
                ? new StepOutcome(OpenStep, Ok: false, 0, result.Status.ToString(), StreamingDetail.NoExtendedError)
                : new StepOutcome(OpenStep, Ok: false, code, NativeCodes.Name(code), result.Status.ToString());
            return new StreamingOpenOutcome(status, deviceId, step);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StreamingOpenOutcome(StreamingOpenStatus.CallFailed, deviceId, StreamingCoordinator.Failure(OpenStep, ex));
        }
    }

    public StreamingReleaseOutcome Release(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (!PlatformGuard.HasAudioPlaybackConnection)
        {
            return new StreamingReleaseOutcome(false, deviceId, StepOutcomes.NotAvailable(ReleaseStep, StreamingDetail.Support));
        }

        IHeldLink? link;
        lock (_gate)
        {
            _links.TryGetValue(deviceId, out link);
        }

        if (link is null)
        {
            return new StreamingReleaseOutcome(false, deviceId, StepOutcomes.NotAttempted(ReleaseStep, StreamingDetail.NothingEnabled));
        }

        // Forgotten only once Windows has confirmed. One it would not close is kept, so the next Release tries the
        // same connection again, and the step that says why goes back to the caller, which logs it and tells the owner.
        StepOutcome step = link.Close();
        if (!step.Ok)
        {
            return new StreamingReleaseOutcome(false, deviceId, step);
        }

        lock (_gate)
        {
            if (_links.TryGetValue(deviceId, out IHeldLink? current) && ReferenceEquals(current, link))
            {
                _links.Remove(deviceId);
            }
        }

        return new StreamingReleaseOutcome(true, deviceId, step);
    }

    // Puts a stand-in connection where a real one would be, for the test of Release above. Nothing in the
    // application calls it.
    internal void HoldForTest(string deviceId, IHeldLink link)
    {
        lock (_gate)
        {
            _links[deviceId] = link;
        }
    }

    // How many connections are held, for the tests only.
    internal int HeldConnections
    {
        get
        {
            lock (_gate)
            {
                return _links.Count;
            }
        }
    }

    [SupportedOSPlatform("windows10.0.10240.0")]
    private static Guid ContainerOf(DeviceInformation information) =>
        information.Properties.TryGetValue(ContainerIdProperty, out object? value) && value is Guid container ? container : Guid.Empty;

    // Windows calls this on a thread of its own. A handler that throws there would be lost inside the
    // projection, so it is caught and logged here instead.
    private void Report(string deviceId, StreamingLinkState state)
    {
        try
        {
            LinkChanged?.Invoke(this, new StreamingLinkChanged(deviceId, state));
        }
        catch (Exception ex)
        {
            _log.Error("Play from a phone: a link change for device " + StreamingLog.Key(deviceId) + " was not handled.", ex);
        }
    }

    // One enabled connection and its StateChanged subscription, kept and let go together. The device id is the
    // one the connection was created from, so a report never depends on how Windows spells it back. The members
    // that call WinRT carry the platform attribute, not the class: the class is only a holder, and it is named
    // by the dictionary above, which exists on every build of Windows.
    private sealed class Link : IHeldLink
    {
        private readonly WindowsStreamingPlatform _owner;
        private readonly string _deviceId;

        [SupportedOSPlatform("windows10.0.19041.0")]
        public Link(WindowsStreamingPlatform owner, string deviceId, AudioPlaybackConnection connection)
        {
            _owner = owner;
            _deviceId = deviceId;
            Connection = connection;
        }

        public AudioPlaybackConnection Connection { get; }

        // Its own step, not part of construction: subscribing is a call into Windows and can fail, and by then the
        // connection must already be held, so that whatever happens it is let go of through Release.
        [SupportedOSPlatform("windows10.0.19041.0")]
        public void Listen() => Connection.StateChanged += OnStateChanged;

        // "Call Dispose to release the reference and free any associated resources."
        [SupportedOSPlatform("windows10.0.19041.0")]
        public StepOutcome Close()
        {
            try
            {
                Connection.StateChanged -= OnStateChanged;
                Connection.Dispose();
                return StepOutcomes.FromHResult(ReleaseStep, 0);
            }
            catch (Exception ex)
            {
                return StreamingCoordinator.Failure(ReleaseStep, ex);
            }
        }

        [SupportedOSPlatform("windows10.0.19041.0")]
        private void OnStateChanged(AudioPlaybackConnection sender, object args)
        {
            StreamingLinkState state;
            try
            {
                state = StreamingStatusMap.FromWinRt(sender.State);
            }
            catch (Exception ex)
            {
                _owner._log.Warn("Play from a phone: " + StreamingLog.Describe(StreamingCoordinator.Failure("streaming-state", ex)));
                return;
            }

            _owner.Report(_deviceId, state);
        }
    }
}
