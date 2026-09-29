using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Whether anything is rendering to the AirPods right now. Playing: at least one audio session on a render endpoint of
// the container is active. Silent: the read worked and none is. Unknown: it could not be read; the steps say why.
internal enum RenderActivityState
{
    Unknown,
    Silent,
    Playing,
}

internal sealed record RenderActivityReading(RenderActivityState State, int ActiveSessions, IReadOnlyList<StepOutcome> Steps);

// Reads render activity on the AirPods' endpoint, and only reads it: nothing is opened, started, stopped or changed.
// The one real implementation is in Earshot.Audio (CoreAudioRenderActivity); tests use a fake.
internal interface IRenderActivity
{
    Task<RenderActivityReading> ReadAsync(Guid containerId, CancellationToken ct);
}
