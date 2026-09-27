using Earshot.Contracts;

namespace Earshot.Widget.Alert;

// Shows the low battery alert. ToastNotifier is the real route; CardNotifier is both the fallback when a
// toast cannot be shown and the whole notifier in safe mode or with a redirected data root.
internal interface INotifier
{
    Task<StepOutcome> NotifyAsync(string title, string text);
}
