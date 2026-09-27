using Earshot.Contracts;
using Earshot.Widget.Alert;

namespace Earshot.Tests.Widget;

// No toast and no card behind it. Records every call.
internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Text)> Calls { get; } = new();

    public StepOutcome Result { get; set; } = StepOutcomes.FromHResult("fake-notify", 0);

    public Task<StepOutcome> NotifyAsync(string title, string text)
    {
        Calls.Add((title, text));
        return Task.FromResult(Result);
    }
}
