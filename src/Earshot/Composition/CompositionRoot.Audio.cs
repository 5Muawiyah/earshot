using Earshot.Audio;

namespace Earshot.Composition;

// Discovery: the audio worker (the one MTA apartment for Core Audio work) and the device monitor. The
// monitor is not started here; the tray starts it, and probe refreshes it without subscribing. Both are
// read-only, so safe mode leaves them as they are.
internal static partial class CompositionRoot
{
    static partial void ConfigureAudio(ServiceRegistry r)
    {
        var worker = new AudioWorker(r.Log);
        r.Worker = worker;
        r.Monitor = new CoreAudioDeviceMonitor(worker, new CoreAudioEndpointSource(worker), r.Settings, r.Log, r.UiPost);
    }
}
