using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Widget.EarPause;

namespace Earshot.Audio;

// Reads whether anything is rendering to the AirPods: an active audio session on a render endpoint of the device's
// container. Runs on the audio worker (the one MTA apartment for Core Audio work) and only reads: it lists endpoints,
// opens each one's session manager and asks every session for its state. It starts, stops and changes nothing, and
// registers no listener.
//
// "Active" is the audio session's own word: the session has an active audio stream. A player that has paused usually
// stops its stream, and the session then reads inactive; a player that keeps a silent stream running still reads
// active. That is the best account of "playing to the AirPods" the audio side gives. It is not a level meter, so a
// quiet passage is not taken for silence. The system sounds session is left out: a notification ping is not playback.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-sessions
internal sealed class CoreAudioRenderActivity : IRenderActivity
{
    internal const string EnumerateStep = "render-activity-enumerate";
    internal const string DeviceStep = "render-activity-device";
    internal const string ManagerStep = "render-activity-session-manager";
    internal const string SessionEnumStep = "render-activity-session-enumerator";
    internal const string CountStep = "render-activity-session-count";
    internal const string SessionStep = "render-activity-session";
    internal const string StateStep = "render-activity-session-state";
    internal const string SystemSoundsStep = "render-activity-system-sounds";

    private readonly AudioWorker _worker;

    public CoreAudioRenderActivity(AudioWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
    }

    public Task<RenderActivityReading> ReadAsync(Guid containerId, CancellationToken ct) =>
        _worker.RunAsync(_ => Read(containerId), ct);

    private RenderActivityReading Read(Guid containerId)
    {
        int hr = _worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return Combine(0, [StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr < 0 ? hr : CoreAudio.E_POINTER)]);
        }

        return ReadFrom(enumerator, containerId, ComRelease.Rcw);
    }

    // The enumerator stays the caller's. release is called once for every COM object this read obtains.
    internal static RenderActivityReading ReadFrom(IMMDeviceEnumerator enumerator, Guid containerId, Action<object> release)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(release);

        EndpointEnumeration endpoints = CoreAudioEndpointReader.ReadAll(enumerator, release);
        if (!endpoints.Ok)
        {
            return Combine(0, endpoints.Steps);
        }

        var steps = new List<StepOutcome>();
        int active = 0;
        foreach (EndpointReading reading in endpoints.Readings)
        {
            AudioEndpoint endpoint = reading.Endpoint;
            if (endpoint.Flow != EndpointFlow.Render || endpoint.State != EndpointState.Active || endpoint.ContainerId != containerId)
            {
                continue;
            }

            int hr = enumerator.GetDevice(endpoint.EndpointId, out IMMDevice? device);
            if (hr < 0 || device is null)
            {
                steps.Add(StepOutcomes.FromHResult(DeviceStep, hr < 0 ? hr : CoreAudio.E_POINTER));
                continue;
            }

            try
            {
                active += ReadEndpoint(device, steps, release);
            }
            finally
            {
                release(device);
            }
        }

        return Combine(active, steps);
    }

    // The active sessions of one endpoint. A step that failed is added to steps; whatever was read before it still
    // counts towards the total.
    private static int ReadEndpoint(IMMDevice device, List<StepOutcome> steps, Action<object> release)
    {
        int hr = CoreAudio.Activate<IAudioSessionManager2>(device, out IAudioSessionManager2? manager);
        if (hr < 0 || manager is null)
        {
            steps.Add(StepOutcomes.FromHResult(ManagerStep, hr < 0 ? hr : CoreAudio.E_POINTER));
            return 0;
        }

        try
        {
            hr = manager.GetSessionEnumerator(out nint enumeratorPointer);
            hr = ComActivation.TakeInterface(hr, enumeratorPointer, out IAudioSessionEnumerator? sessions);
            if (hr < 0 || sessions is null)
            {
                steps.Add(StepOutcomes.FromHResult(SessionEnumStep, hr < 0 ? hr : CoreAudio.E_POINTER));
                return 0;
            }

            try
            {
                return ReadSessions(sessions, steps, release);
            }
            finally
            {
                release(sessions);
            }
        }
        finally
        {
            release(manager);
        }
    }

    private static int ReadSessions(IAudioSessionEnumerator sessions, List<StepOutcome> steps, Action<object> release)
    {
        int hr = sessions.GetCount(out int count);
        if (hr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(CountStep, hr));
            return 0;
        }

        int active = 0;
        for (int index = 0; index < count; index++)
        {
            hr = sessions.GetSession(index, out nint sessionPointer);
            hr = ComActivation.TakeInterface(hr, sessionPointer, out IAudioSessionControl2? session);
            if (hr < 0 || session is null)
            {
                steps.Add(StepOutcomes.FromHResult(SessionStep + ":" + index, hr < 0 ? hr : CoreAudio.E_POINTER));
                continue;
            }

            try
            {
                // S_OK is the system sounds session, S_FALSE any other; a failure is neither.
                hr = session.IsSystemSoundsSession();
                if (hr < 0)
                {
                    steps.Add(StepOutcomes.FromHResult(SystemSoundsStep + ":" + index, hr));
                        continue;
                }

                if (hr == 0)
                {
                    continue;
                }

                hr = session.GetState(out int state);
                if (hr < 0)
                {
                    steps.Add(StepOutcomes.FromHResult(StateStep + ":" + index, hr));
                        continue;
                }

                if (state == AudioSessionState.Active)
                {
                    active++;
                }
            }
            finally
            {
                release(session);
            }
        }

        return active;
    }

    // Playing when a session is active, whatever else failed: an active stream is seen. Otherwise Silent only when
    // every read worked; a read that failed and found nothing says nothing.
    internal static RenderActivityReading Combine(int active, IReadOnlyList<StepOutcome> steps)
    {
        if (active > 0)
        {
            return new RenderActivityReading(RenderActivityState.Playing, active, steps);
        }

        return steps.Any(step => !step.Ok)
            ? new RenderActivityReading(RenderActivityState.Unknown, 0, steps)
            : new RenderActivityReading(RenderActivityState.Silent, 0, steps);
    }
}
