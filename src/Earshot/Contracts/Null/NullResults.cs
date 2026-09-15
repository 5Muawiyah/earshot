namespace Earshot.Contracts.Null;

// Shared wording for the null objects, so an unwired feature always says the same thing.
public static class NullResults
{
    public const string NotAvailableMessage = "Not available in this build.";

    // No native call was made, so the step carries NativeCodes.NotAvailable rather than 0 (S_OK).
    public static StepOutcome NotAvailableStep(string step) =>
        StepOutcomes.NotAvailable(step, NotAvailableMessage);

    public static ControllerResult NotAttempted(string step) =>
        new(OpStatus.NotAttempted, NotAvailableMessage, new[] { NotAvailableStep(step) });

    public static ConnectResult ConnectNotAttempted(string step) =>
        new(ConnectOutcome.Failed, NotAvailableMessage, new[] { NotAvailableStep(step) });
}
