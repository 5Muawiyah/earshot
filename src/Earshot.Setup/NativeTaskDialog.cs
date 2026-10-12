using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Earshot.Setup;

// TaskDialogIndirect, called directly: Setup has no WinForms, and a task dialog is what Windows itself uses for a message with
// choices. The structures are packed to one byte, as commctrl.h declares them (pshpack1.h around TASKDIALOGCONFIG and
// TASKDIALOG_BUTTON).
// https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-taskdialogindirect
// https://learn.microsoft.com/en-us/windows/win32/api/commctrl/ns-commctrl-taskdialogconfig
// https://learn.microsoft.com/en-us/windows/win32/api/commctrl/ns-commctrl-taskdialog_button
// https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nc-commctrl-pftaskdialogcallback
internal static unsafe partial class NativeTaskDialog
{
    public const int Ok = 1;
    public const int Cancel = 2;

    internal const uint FlagUseCommandLinks = 0x10;
    internal const uint FlagMarqueeProgressBar = 0x400;
    internal const uint FlagCallbackTimer = 0x800;
    internal const uint FlagAllowDialogCancellation = 0x8;

    internal const uint CommonOk = 0x1;
    internal const uint CommonCancel = 0x8;

    // MAKEINTRESOURCEW(-1) to (-3).
    internal const nint IconWarning = 0xFFFF;
    internal const nint IconError = 0xFFFE;
    internal const nint IconInformation = 0xFFFD;

    private const uint WmUser = 0x400;
    private const uint TdmClickButton = WmUser + 102;
    private const uint TdmSetProgressBarMarquee = WmUser + 107;
    private const uint TdmEnableButton = WmUser + 111;
    private const uint TdnCreated = 0;
    private const uint TdnTimer = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Config
    {
        public uint CbSize;
        public nint HwndParent;
        public nint HInstance;
        public uint Flags;
        public uint CommonButtons;
        public nint WindowTitle;
        public nint MainIcon;
        public nint MainInstruction;
        public nint Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public nint VerificationText;
        public nint ExpandedInformation;
        public nint ExpandedControlText;
        public nint CollapsedControlText;
        public nint FooterIcon;
        public nint Footer;
        public nint Callback;
        public nint CallbackData;
        public uint Width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Button
    {
        public int Id;
        public nint Text;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(Config* config, int* button, int* radioButton, int* verificationChecked);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    // What the callback needs: whether the work behind a waiting screen has ended.
    private sealed class State(Func<bool>? done)
    {
        public Func<bool>? Done { get; } = done;
    }

    public sealed record Choice(int Id, string Title, string Note);

    // A message with one button.
    public static void Message(nint icon, string title, string instruction, string content)
    {
        Show(icon, title, instruction, content, "", [], CommonOk, 0, waitFor: null);
    }

    // Command links. Returns the chosen id, or Cancel for the close button or the close box.
    public static int Links(string title, string instruction, string content, string footer, IReadOnlyList<Choice> choices)
    {
        return Show(IconInformation, title, instruction, content, footer, choices, CommonCancel, FlagUseCommandLinks | FlagAllowDialogCancellation, waitFor: null);
    }

    // A waiting screen with a moving bar and nothing to click. It stays up until done() returns true; the callback checks
    // about five times a second.
    public static void Waiting(string title, string instruction, string content, Func<bool> done)
    {
        Show(IconInformation, title, instruction, content, "", [], CommonOk, FlagMarqueeProgressBar | FlagCallbackTimer, done);
    }

    private static int Show(
        nint icon, string title, string instruction, string content, string footer, IReadOnlyList<Choice> choices,
        uint commonButtons, uint flags, Func<bool>? waitFor)
    {
        var allocations = new List<nint>();
        nint Text(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }

            nint p = Marshal.StringToHGlobalUni(text);
            allocations.Add(p);
            return p;
        }

        GCHandle stateHandle = default;
        nint buttons = 0;
        try
        {
            var config = new Config
            {
                CbSize = (uint)sizeof(Config),
                Flags = flags,
                CommonButtons = commonButtons,
                WindowTitle = Text(title),
                MainIcon = icon,
                MainInstruction = Text(instruction),
                Content = Text(content),
                Footer = Text(footer),
                FooterIcon = footer.Length > 0 ? IconInformation : 0,
            };

            if (choices.Count > 0)
            {
                buttons = (nint)NativeMemory.Alloc((nuint)(choices.Count * sizeof(Button)));
                for (int i = 0; i < choices.Count; i++)
                {
                    string label = choices[i].Note.Length > 0 ? choices[i].Title + "\n" + choices[i].Note : choices[i].Title;
                    ((Button*)buttons)[i] = new Button { Id = choices[i].Id, Text = Text(label) };
                }

                config.ButtonCount = (uint)choices.Count;
                config.Buttons = buttons;
            }

            if (waitFor is not null)
            {
                stateHandle = GCHandle.Alloc(new State(waitFor));
                config.Callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint, int>)&Callback;
                config.CallbackData = GCHandle.ToIntPtr(stateHandle);
            }

            int pressed = 0;
            int hr = TaskDialogIndirect(&config, &pressed, null, null);
            if (hr < 0)
            {
                throw new InvalidOperationException("TaskDialogIndirect failed with 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            return pressed;
        }
        finally
        {
            if (stateHandle.IsAllocated)
            {
                stateHandle.Free();
            }

            if (buttons != 0)
            {
                NativeMemory.Free((void*)buttons);
            }

            foreach (nint p in allocations)
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }

    // Runs on the dialog's thread. It makes the OK button, which a waiting screen has no use for, unavailable and starts the
    // bar moving; then, on each timer notification, closes the dialog once the work has ended.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Callback(nint hwnd, uint message, nuint wParam, nint lParam, nint data)
    {
        if (data == 0 || GCHandle.FromIntPtr(data).Target is not State { Done: { } done })
        {
            return 0;
        }

        if (message == TdnCreated)
        {
            SendMessage(hwnd, TdmEnableButton, Ok, 0);
            SendMessage(hwnd, TdmSetProgressBarMarquee, 1, 30);
        }
        else if (message == TdnTimer && done())
        {
            SendMessage(hwnd, TdmEnableButton, Ok, 1);
            SendMessage(hwnd, TdmClickButton, Ok, 0);
        }

        return 0;
    }
}
