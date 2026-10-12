using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Earshot.Repair;

// Consent for Fix connection comes from a person at the screen answering a dialog, and from nothing else: no flag, no
// environment variable, no line on standard input and no setting makes one. FixConsent has only a private constructor and one
// non-public factory, and only FixConsentDialog calls the factory, which it does after FixConsentDialog.Ask* has had a
// definite OK from the prompter. A test reads the assembly for any other caller.
//
// This keeps accidents and scripts out. It is not a boundary against another program of the same user: that program can
// press a dialog's button on the same desktop, and can run the elevated mode itself. The two questions stop a script that
// has only a command line, and they tell a person exactly what will happen before it does.

internal enum FixConsentKind
{
    Changes,
    RePair,
}

internal sealed class FixConsent
{
    private FixConsent(FixConsentKind kind)
    {
        Kind = kind;
    }

    public FixConsentKind Kind { get; }

    // Called only from FixConsentDialog.
    internal static FixConsent Issue(FixConsentKind kind) => new(kind);
}

internal enum FixAnswer
{
    Ok,
    Cancel,
    TimedOut,
}

// What one dialog shows. Choices are radio buttons; none is chosen for the person. Lines are the list of changes.
internal sealed record FixPrompt(string Title, string Heading, string Body, IReadOnlyList<string> Lines, IReadOnlyList<string> Choices, string OkText);

// Chosen is the index of the chosen radio button, or -1.
internal sealed record FixPromptAnswer(FixAnswer Answer, int Chosen);

// Shows a prompt and returns the answer. The real one is a TaskDialog; a test has a fake. A prompter cannot make a consent: it
// only reports what was pressed.
internal interface IFixPrompter
{
    FixPromptAnswer Ask(FixPrompt prompt);
}

// What the process can see of the desktop it runs on, and the desktop that receives input.
internal readonly record struct DesktopReading(bool UserInteractive, string? ThreadDesktop, string? InputDesktop, uint InputError);

internal interface IInputDesktop
{
    DesktopReading Read();
}

internal sealed record FixConsentAnswer(FixConsent? Consent, int Chosen);

internal sealed class FixConsentDialog
{
    private readonly IFixPrompter _prompter;
    private readonly IInputDesktop _desktop;

    public FixConsentDialog(IFixPrompter prompter, IInputDesktop desktop)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        ArgumentNullException.ThrowIfNull(desktop);
        _prompter = prompter;
        _desktop = desktop;
    }

    // True when a person can see a dialog: this process runs on the desktop that receives input.
    public bool CanAsk() => FixRules.IsInteractive(_desktop.Read());

    // The first question: every change, then OK or cancel. With several AirPods to pick from, the answer also says which.
    public FixConsentAnswer AskChanges(FixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!CanAsk())
        {
            return new FixConsentAnswer(null, -1);
        }

        FixPromptAnswer answer = _prompter.Ask(FixPromptText.Changes(plan));
        return answer.Answer == FixAnswer.Ok && (!plan.NeedsPick || answer.Chosen >= 0)
            ? new FixConsentAnswer(FixConsent.Issue(FixConsentKind.Changes), answer.Chosen)
            : new FixConsentAnswer(null, answer.Chosen);
    }

    // The second question: remove the AirPods from Windows and pair them again, by name.
    public FixConsent? AskRePair(IReadOnlyList<string> deviceNames, bool removeFirst)
    {
        ArgumentNullException.ThrowIfNull(deviceNames);
        if (!CanAsk())
        {
            return null;
        }

        FixPromptAnswer answer = _prompter.Ask(FixPromptText.RePair(deviceNames, removeFirst));
        return answer.Answer == FixAnswer.Ok ? FixConsent.Issue(FixConsentKind.RePair) : null;
    }
}

// The words of the two questions. British English, short, no identifiers.
internal static class FixPromptText
{
    public const string Title = "Fix connection";

    public static FixPrompt Changes(FixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        foreach (FixDevice device in plan.Devices.Where(d => d.Nodes.Count > 0))
        {
            foreach (FixNodeStep node in device.Nodes)
            {
                lines.Add(device.Name + ": " + (node.Action == FixNodeAction.Enable ? "turn on " : "clear the saved off setting of ") + node.Label.ToLowerInvariant());
            }
        }

        lines.AddRange(plan.Services.Select(s => "Turn " + s.Label + " back on in Windows"));

        string body = plan.NeedsPick
            ? "More than one pair of AirPods is on this PC. Pick the one whose changes should be undone."
            : "Earshot will ask Windows for administrator approval once, then undo only the changes listed here.";
        if (plan.Devices.Any(d => !d.Pinned && d.Nodes.Count > 0))
        {
            body += " These changes may have been Earshot's. Nothing else is touched.";
        }

        return new FixPrompt(
            Title,
            "Undo Earshot's changes",
            body,
            lines,
            plan.NeedsPick ? plan.Devices.Where(d => d.Nodes.Count > 0).Select(d => d.Name).ToList() : [],
            "Make these changes");
    }

    public static FixPrompt RePair(IReadOnlyList<string> deviceNames, bool removeFirst)
    {
        ArgumentNullException.ThrowIfNull(deviceNames);
        string names = deviceNames.Count == 0 ? "your AirPods" : string.Join(" and ", deviceNames);
        if (removeFirst)
        {
            return new FixPrompt(
                Title,
                "Remove the AirPods from Windows and pair them again",
                "Windows will forget " + names + ", then Earshot will look for them so you can pair them again. They stop working with this PC until they are paired.",
                [],
                [],
                "Remove and pair again");
        }

        return new FixPrompt(
            Title,
            "Pair the AirPods again",
            "Earshot will look for Bluetooth audio devices nearby so you can pair your AirPods.",
            [],
            [],
            "Look for them");
    }
}

// The real prompter: a TaskDialog with Cancel as the default button and a timeout that cancels.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.taskdialog
internal sealed class TaskDialogPrompter : IFixPrompter
{
    public FixPromptAnswer Ask(FixPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        TaskDialogPage page = FixDialogPages.Build(prompt, out TimeSpan timeout);
        // The first button is the OK button, as Build adds it.
        TaskDialogButton ok = page.Buttons[0];
        using var timer = new System.Windows.Forms.Timer { Interval = (int)timeout.TotalMilliseconds };
        bool timedOut = false;
        timer.Tick += (_, _) =>
        {
            timedOut = true;
            timer.Stop();
            page.BoundDialog?.Close();
        };
        page.Created += (_, _) => timer.Start();
        page.Destroyed += (_, _) => timer.Stop();

        TaskDialogButton pressed = TaskDialog.ShowDialog(page);
        int chosen = -1;
        for (int i = 0; i < page.RadioButtons.Count; i++)
        {
            if (page.RadioButtons[i].Checked)
            {
                chosen = i;
            }
        }

        FixAnswer answer = timedOut ? FixAnswer.TimedOut : ReferenceEquals(pressed, ok) ? FixAnswer.Ok : FixAnswer.Cancel;
        return new FixPromptAnswer(answer, chosen);
    }
}

internal static class FixDialogPages
{
    // How long a question waits for an answer before it counts as cancelled.
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static TaskDialogPage Build(FixPrompt prompt, out TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var ok = new TaskDialogButton(prompt.OkText);
        var page = new TaskDialogPage
        {
            Caption = prompt.Title,
            Heading = prompt.Heading,
            Text = prompt.Body + (prompt.Lines.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, prompt.Lines)),
            Icon = TaskDialogIcon.Shield,
            AllowCancel = true,
        };
        foreach (string choice in prompt.Choices)
        {
            page.RadioButtons.Add(new TaskDialogRadioButton(choice));
        }

        // One Cancel instance: TaskDialogButton.Cancel makes a new button on each read, and the default button must be one
        // the page holds.
        TaskDialogButton cancel = TaskDialogButton.Cancel;
        page.Buttons.Add(ok);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        timeout = Timeout;
        return page;
    }
}

// The desktop checks. A program on a private desktop, or in a service session, has a thread desktop that is not the one
// receiving input, and prints its plan instead of asking.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw
internal sealed partial class WindowsInputDesktop : IInputDesktop
{
    private const int UoiName = 2;
    private const uint DesktopReadObjects = 0x0001;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformation(nint handle, int index, char* info, uint length, out uint needed);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    public DesktopReading Read()
    {
        // The thread's desktop handle belongs to the thread and is not closed.
        string? thread = NameOf(GetThreadDesktop(GetCurrentThreadId()), out uint threadError);
        nint input = OpenInputDesktop(0, false, DesktopReadObjects);
        if (input == 0)
        {
            return new DesktopReading(Environment.UserInteractive, thread, null, unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        try
        {
            string? name = NameOf(input, out uint error);
            return new DesktopReading(Environment.UserInteractive, thread, name, name is null ? error : 0);
        }
        finally
        {
            if (!CloseDesktop(input))
            {
                System.Diagnostics.Trace.WriteLine("Earshot: CloseDesktop failed with Win32 error " + Marshal.GetLastPInvokeError() + " (thread desktop read " + threadError + ").");
            }
        }
    }

    private static unsafe string? NameOf(nint desktop, out uint error)
    {
        error = 0;
        if (desktop == 0)
        {
            error = unchecked((uint)Marshal.GetLastPInvokeError());
            return null;
        }

        char* buffer = stackalloc char[256];
        if (!GetUserObjectInformation(desktop, UoiName, buffer, 256 * sizeof(char), out _))
        {
            error = unchecked((uint)Marshal.GetLastPInvokeError());
            return null;
        }

        return new string(buffer);
    }
}
