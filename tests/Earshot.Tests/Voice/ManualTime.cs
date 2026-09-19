namespace Earshot.Tests.Voice;

// A clock that only moves when the test moves it, the same shape as the other ManualTime fakes in this
// suite (tests\Earshot.Tests\Integration\Coordinator\CoordinatorHarness.cs, tests\Earshot.Tests\Phase4\
// Fakes.cs), trimmed to what SpeechAnnouncer needs: GetUtcNow and Advance. CreateTimer is not used by
// SpeechAnnouncer, so it is left unimplemented rather than given a fake timer queue no test here needs.
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
