using Earshot.Contracts;
using Earshot.Widget.Alert;

namespace Earshot.Tests.Widget;

// No toast and no card behind it. Records every call.
internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Text)> Calls { get; } = new();

    public StepOutcome Result { get; set; } = StepOutcomes.FromHResult("fake-notify", 0);

    // The compact row of each call, in the same order as Calls.
    public List<CompactLine?> Lines { get; } = new();

    public Task<StepOutcome> NotifyAsync(string title, string text, CompactLine? line = null)
    {
        Calls.Add((title, text));
        Lines.Add(line);
        return Task.FromResult(Result);
    }
}
