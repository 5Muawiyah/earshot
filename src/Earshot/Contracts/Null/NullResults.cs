namespace Earshot.Contracts.Null;

// Shared wording for the null objects, so an unwired feature always says the same thing.
public static class NullResults
{
    public const string NotAvailableMessage = "Not available in this build.";

    public static StepOutcome NotAvailableStep(string step) =>
        new(step, Ok: false, Code: 0, CodeName: "NOT_AVAILABLE", Detail: NotAvailableMessage);

    public static ControllerResult NotAttempted(string step) =>
        new(OpStatus.NotAttempted, NotAvailableMessage, new[] { NotAvailableStep(step) });

    public static ConnectResult ConnectNotAttempted(string step) =>
        new(ConnectOutcome.Failed, NotAvailableMessage, new[] { NotAvailableStep(step) });
}
