namespace Earshot.Contracts.Null;

// Stands in until audio protection is wired. The state is Unknown, never guessed.
public sealed class NullAudioProtectionController : IAudioProtectionController
{
    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new AudioProtectionSnapshot(
            State: AudioProtectionState.Unknown,
            HandsfreeInstalled: false,
            HeadsetInstalled: false));

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) =>
        Task.FromResult(NullResults.NotAttempted(protect ? "protect-on" : "protect-off"));
}
