using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Tray;

// Log text for controller results, so every failed step is written with its decoded code.
internal static class TrayReport
{
    public static string Describe(string action, OpStatus status, string userMessage, IReadOnlyList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(steps);

        var text = new StringBuilder();
        text.Append(action).Append(": ").Append(status).Append(". ").Append(userMessage);
        foreach (StepOutcome step in steps)
        {
            text.Append(" | ").Append(DescribeStep(step));
        }

        return text.ToString();
    }

    public static string DescribeStep(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        string text = step.Step + " " + (step.Ok ? "ok" : "failed") + " " + step.CodeName +
            " (0x" + unchecked((uint)step.Code).ToString("X8", CultureInfo.InvariantCulture) + ")";
        return step.Detail is null ? text : text + ": " + step.Detail;
    }
}
