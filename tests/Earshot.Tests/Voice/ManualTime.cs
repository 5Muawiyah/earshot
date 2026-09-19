namespace Earshot.Tests.Voice;

// A clock that only moves when the test moves it, the same shape as the other ManualTime fakes in this
// suite (tests\Earshot.Tests\Integration\Coordinator\CoordinatorHarness.cs, tests\Earshot.Tests\Phase4\
// Fakes.cs), trimmed to what SpeechAnnouncer needs: GetUtcNow for anything that still wants the wall
// clock, and GetTimestamp/TimestampFrequency for the monotonic repeat-gap clock SpeechAnnouncer actually
// measures against. The two are tracked separately and only Advance moves both together, so a test can
// prove the repeat gap ignores the wall clock (RepeatGapUsesTheMonotonicClockNotTheWallClock in
// SpeechAnnouncerTests) by moving one without the other. CreateTimer is not used by SpeechAnnouncer, so
// it is left unimplemented rather than given a fake timer queue no test here needs.
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    // TimestampFrequency ticks per second equal to TimeSpan.Ticks per second makes GetTimestamp() values
    // interpretable directly as ticks, so Advance can move both clocks by exactly the same amount with
    // no unit conversion to get wrong.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    // Advances both the wall clock and the monotonic clock together, the ordinary case: real time
    // passing moves both.
    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }

    // Moves only the wall clock, as a system clock correction would (NTP, the owner changing it by
    // hand): the monotonic clock GetTimestamp() drives is untouched, which is the point of the test that
    // uses this.
    public void StepWallClockBackwardOnly(TimeSpan by) => _now -= by;
}
