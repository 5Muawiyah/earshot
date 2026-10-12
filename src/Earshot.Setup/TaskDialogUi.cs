namespace Earshot.Setup;

// The screens, as task dialogs.
internal sealed class TaskDialogUi : ISetupUi
{
    private const int FirstChoiceId = 1000;

    public SetupAction ChooseAction(string instruction, string content, string footer, IReadOnlyList<SetupAction> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var choices = new List<NativeTaskDialog.Choice>();
        for (int i = 0; i < offered.Count; i++)
        {
            (string title, string note) = Label(offered[i]);
            choices.Add(new NativeTaskDialog.Choice(FirstChoiceId + i, title, note));
        }

        int pressed = NativeTaskDialog.Links(SetupText.WindowTitle, instruction, content, footer, choices);
        int index = pressed - FirstChoiceId;
        return index >= 0 && index < offered.Count ? offered[index] : SetupAction.Cancel;
    }

    public int? ChooseAirPods(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var choices = new List<NativeTaskDialog.Choice>();
        for (int i = 0; i < names.Count; i++)
        {
            choices.Add(new NativeTaskDialog.Choice(FirstChoiceId + i, names[i], ""));
        }

        int pressed = NativeTaskDialog.Links(SetupText.WindowTitle, SetupText.WhichAirPodsInstruction, SetupText.WhichAirPodsContent, "", choices);
        int index = pressed - FirstChoiceId;
        return index >= 0 && index < names.Count ? index : null;
    }

    public void Show(MessageKind kind, string instruction, string content) =>
        NativeTaskDialog.Message(
            kind == MessageKind.Error ? NativeTaskDialog.IconError : NativeTaskDialog.IconInformation,
            SetupText.WindowTitle, instruction, content);

    public T Work<T>(string instruction, string content, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        T? result = default;
        Exception? failure = null;
        int finished = 0;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                // Handed back to the caller's thread below, which rethrows it with its stack.
                failure = ex;
            }
            finally
            {
                Volatile.Write(ref finished, 1);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        NativeTaskDialog.Waiting(SetupText.WindowTitle, instruction, content, () => Volatile.Read(ref finished) == 1);
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    private static (string Title, string Note) Label(SetupAction action) => action switch
    {
        SetupAction.Install => (SetupText.ChoiceInstall, ""),
        SetupAction.Repair => (SetupText.ChoiceRepair, SetupText.ChoiceRepairNote),
        SetupAction.FixConnection => (SetupText.ChoiceFix, SetupText.ChoiceFixNote),
        SetupAction.Uninstall => (SetupText.ChoiceUninstall, SetupText.ChoiceUninstallNote),
        _ => ("Cancel", ""),
    };
}
