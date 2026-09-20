using Earshot.TestWindow.Core;

namespace Earshot.Tests.TestWindow;

// The half markers from test-gui.md section 6.1's table, as TestRowSpec values. Shared by
// StateDeriverTests, StateDeriverPinTests and StateDeriverPropertyTests so the same shapes are
// used everywhere rather than approximated per test.
internal static class TestRowSpecFixtures
{
    internal static TestRowSpec OneHalf(string testId = "01-a2dp-oneshot") => new()
    {
        TestId = testId,
        Halves = 1,
    };

    internal static TestRowSpec Test04() => new()
    {
        TestId = "04-block-and-reboot",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.AnyStart,
        FirstHalfOnlyCriteriaIds = new[] { "block", "disabled-now", "locatable" },
        SecondHalfOnlyCriteriaIds = new[] { "persisted", "stayed-on-phone" },
    };

    internal static TestRowSpec Test05() => new()
    {
        TestId = "05-allow",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.AnyStart,
        FirstHalfOnlyCriteriaIds = new[] { "allow", "problem-cleared", "bit-cleared", "endpoints-back" },
        SecondHalfOnlyCriteriaIds = new[] { "still-allowed" },
        FirstHalfAloneIsCompleteWhenDeclined = true,
    };

    internal static TestRowSpec Test08() => new()
    {
        TestId = "08-acceptance-power-cycle",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.FullShutDown,
        FirstHalfOnlyCriteriaIds = new[] { "default-config", "blocked-before-power-cycle" },
        SecondHalfOnlyCriteriaIds = new[] { "ACCEPTANCE" },
    };

    internal static TestRowSpec Test09() => new()
    {
        TestId = "09-shutdown-while-connected",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.FullShutDown,
        FirstHalfOnlyCriteriaIds = new[] { "connected-first" },
        SecondHalfOnlyCriteriaIds = new[] { "not-paged-at-boot", "nodes-after-boot", "end-session-logged" },
    };

    internal static TestRowSpec Test10Variant1() => new()
    {
        TestId = "10-shutdown-messages-v1",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.Restart,
        FirstHalfOnlyFindingNames = new[] { "stateBeforeRestart" },
        SecondHalfOnlyCriteriaIds = new[] { "query-arrived", "end-arrived", "block-queued" },
    };

    internal static TestRowSpec Test15() => new()
    {
        TestId = "15-uninstall-reversal",
        Halves = 2,
        PowerCycleRequirement = PowerCycleRequirement.AnyStart,
        FirstHalfOnlyCriteriaIds = new[] { "uninstall", "install-again" },
        SecondHalfOnlyCriteriaIds = new[] { "delayed-deletion" },
    };
}
