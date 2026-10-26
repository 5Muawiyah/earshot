using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The consent boundary. A consent is an object only FixConsentDialog makes, from an answer on the screen. The checks here are a
// guard against accidents and against scripts that pass a flag, an environment variable or a line on standard input. They are
// not a boundary against another process of the same user: such a process can drive a dialog on the same desktop, and these
// tests do not claim otherwise.
[TestClass]
public sealed class FixConsentTests
{
    private static readonly string EmDash = ((char)0x2014).ToString();

    internal static readonly string[] YesFlags =
    [
        "--yes", "-y", "/y", "--force", "-f", "--consent", "--ok", "--accept", "--assume-yes", "--confirm", "--no-prompt", "--noprompt",
        "--silent", "/quiet", "--unattended", "--non-interactive", "--consent=yes", "--yes=true", "YES", "y",
    ];

    internal static readonly string[] YesVariables =
    [
        "EARSHOT_YES", "EARSHOT_CONSENT", "EARSHOT_FORCE", "EARSHOT_ASSUME_YES", "EARSHOT_FIX_CONSENT", "CONSENT", "YES", "FORCE", "ASSUME_YES", "CI",
    ];

    private static async Task<(FixResult Result, FixRig Rig)> RunAsync(IReadOnlyList<string> args, bool interactive, string? stdin = null)
    {
        var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        if (!interactive)
        {
            rig.Desktop = FakeDesktop.NoOneThere();
        }

        TextReader originalIn = Console.In;
        var scopes = new List<EnvironmentVariableScope>();
        try
        {
            if (stdin is not null)
            {
                Console.SetIn(new StringReader(stdin));
            }

            scopes.AddRange(YesVariables.Select(v => new EnvironmentVariableScope(v, "1")));
            FixResult result = await FixConnection.RunAsync(args, rig.Host(), CancellationToken.None);
            return (result, rig);
        }
        finally
        {
            Console.SetIn(originalIn);
            foreach (EnvironmentVariableScope s in scopes)
            {
                s.Dispose();
            }
        }
    }

    private static void AssertNothingHappened(FixRig rig)
    {
        Assert.IsEmpty(rig.Prompter.Asked, "A dialog was shown.");
        Assert.IsEmpty(rig.Elevator.Calls, "Elevation was requested.");
        Assert.IsEmpty(rig.Pairing.Calls, "The pairing API was called.");
        Assert.IsEmpty(rig.GateFake.Calls, "The gate was called.");
        Assert.IsEmpty(rig.Gate.Nodes.Calls, "A node was changed.");
        Assert.IsFalse(Directory.Exists(rig.PlanFiles.Root) && Directory.EnumerateFiles(rig.PlanFiles.Root, "*", SearchOption.AllDirectories).Any(), "A plan file was written.");
    }

    [TestMethod]
    public async Task EveryYesForceOrConsentFlagIsRefusedAndNothingChanges()
    {
        foreach (string flag in YesFlags)
        {
            (FixResult result, FixRig rig) = await RunAsync([flag], interactive: true, stdin: "y\ny\ny\n");
            using (rig)
            {
                Assert.AreEqual(FixOutcome.Usage, result.Outcome, flag);
                Assert.AreEqual(ExitCodes.Usage, result.ExitCode, flag);
                AssertNothingHappened(rig);
            }
        }
    }

    [TestMethod]
    public async Task FlagsInEveryPositionAndWithAValidOptionAreStillRefused()
    {
        foreach (string flag in YesFlags)
        {
            (FixResult result, FixRig rig) = await RunAsync(["--out", Path.Combine(Path.GetTempPath(), "x.txt"), flag], interactive: true);
            using (rig)
            {
                Assert.AreEqual(ExitCodes.Usage, result.ExitCode, flag);
                AssertNothingHappened(rig);
            }
        }
    }

    [TestMethod]
    public async Task YesVariablesAndAYOnStandardInputChangeNothingWhenNobodyIsAtTheScreen()
    {
        (FixResult result, FixRig rig) = await RunAsync([], interactive: false, stdin: "y\nyes\ny\n");
        using (rig)
        {
            Assert.AreEqual(FixOutcome.PlanOnly, result.Outcome);
            Assert.AreEqual(ExitCodes.PlanOnly, result.ExitCode);
            AssertNothingHappened(rig);
        }
    }

    [TestMethod]
    public async Task YesVariablesAndAYOnStandardInputDoNotAnswerADialogEither()
    {
        // Somebody is at the screen, the dialog is shown and cancelled: the variables and the y change nothing.
        var rig = new FixRig();
        using (rig)
        {
            rig.LeaveAirPodsBlocked();
            rig.Prompter.SayCancel();
            var scopes = YesVariables.Select(v => new EnvironmentVariableScope(v, "1")).ToList();
            TextReader originalIn = Console.In;
            try
            {
                Console.SetIn(new StringReader("y\ny\n"));
                FixResult result = await FixConnection.RunAsync([], rig.Host(), CancellationToken.None);
                Assert.AreEqual(FixOutcome.Declined, result.Outcome);
            }
            finally
            {
                Console.SetIn(originalIn);
                scopes.ForEach(s => s.Dispose());
            }

            Assert.HasCount(1, rig.Prompter.Asked);
            Assert.IsEmpty(rig.Elevator.Calls);
            Assert.IsEmpty(rig.Pairing.Calls);
            Assert.IsEmpty(rig.Gate.Nodes.Calls);
        }
    }

    [TestMethod]
    public async Task APlanOnlyRunPrintsThePlanAndStopsBeforeAnything()
    {
        (FixResult result, FixRig rig) = await RunAsync([], interactive: false);
        using (rig)
        {
            string output = rig.Output.ToString();
            Assert.AreEqual(ExitCodes.PlanOnly, result.ExitCode);
            Assert.Contains("AirPods", output, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", output, StringComparison.Ordinal);
            Assert.DoesNotContain(RecordedNodes.AirPodsAddress, output, StringComparison.OrdinalIgnoreCase);
            AssertNothingHappened(rig);
            Assert.AreEqual(1, rig.Desktop.Reads);
        }
    }

    [TestMethod]
    public void NoInputDesktopNothingToCompareOrAnotherDesktopIsNotInteractive()
    {
        Assert.IsTrue(FixRules.IsInteractive(new DesktopReading(true, "Default", "Default", 0)));
        Assert.IsTrue(FixRules.IsInteractive(new DesktopReading(true, "default", "Default", 0)));
        Assert.IsFalse(FixRules.IsInteractive(new DesktopReading(false, "Default", "Default", 0)), "UserInteractive false");
        Assert.IsFalse(FixRules.IsInteractive(new DesktopReading(true, "Private", "Default", 0)), "another desktop");
        Assert.IsFalse(FixRules.IsInteractive(new DesktopReading(true, "Default", null, 5)), "no input desktop");
        Assert.IsFalse(FixRules.IsInteractive(new DesktopReading(true, null, "Default", 0)), "no thread desktop");
        Assert.IsFalse(FixRules.IsInteractive(new DesktopReading(true, "", "", 0)), "empty names");
    }

    // ---- the consent object ----

    [TestMethod]
    public void ConsentHasNoConstructorAnyoneCanCall()
    {
        ConstructorInfo[] all = typeof(FixConsent).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotEmpty(all);
        Assert.IsTrue(all.All(c => c.IsPrivate), "Every constructor of FixConsent is private.");
        Assert.IsFalse(typeof(FixConsent).IsVisible, "FixConsent is not part of any public surface.");
        Assert.IsTrue(typeof(FixConsent).IsSealed);
        // One way in: a static factory that is not public.
        MethodInfo[] factories = typeof(FixConsent).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(FixConsent)).ToArray();
        Assert.HasCount(1, factories);
        Assert.IsFalse(factories[0].IsPublic);
    }

    [TestMethod]
    public void OnlyTheDialogClassCallsTheFactory()
    {
        // A guard against accidents: it reads the IL of every method in the assembly for a call to the factory. A call made by
        // reflection or built at run time is not seen, and a process of the same user can drive a dialog, so this is not a
        // boundary against malware.
        MethodInfo factory = typeof(FixConsent).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Single(m => m.ReturnType == typeof(FixConsent));
        int token = factory.MetadataToken;
        var callers = new List<string>();
        foreach (Type type in typeof(FixConsent).Assembly.GetTypes())
        {
            foreach (MethodBase method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null || !CallsToken(il, token))
                {
                    continue;
                }

                Type owner = type;
                while (owner.DeclaringType is not null)
                {
                    owner = owner.DeclaringType;
                }

                callers.Add(owner.Name);
            }
        }

        Assert.IsNotEmpty(callers, "Nothing calls the factory, so the scan proves nothing.");
        Assert.IsTrue(callers.All(c => c == nameof(FixConsentDialog)), "Called from: " + string.Join(", ", callers.Distinct()));
    }

    private static bool CallsToken(byte[] il, int token)
    {
        byte[] t = BitConverter.GetBytes(token);
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if ((il[i] == OpCodes.Call.Value || il[i] == OpCodes.Callvirt.Value) && il[i + 1] == t[0] && il[i + 2] == t[1] && il[i + 3] == t[2] && il[i + 4] == t[3])
            {
                return true;
            }
        }

        return false;
    }

    [TestMethod]
    public void TheElevatorAndTheRePairerAskForAConsentInTheirSignature()
    {
        Assert.IsTrue(typeof(IFixElevator).GetMethod(nameof(IFixElevator.ApplyAsync))!.GetParameters().Any(p => p.ParameterType == typeof(FixConsent)));
        Assert.IsTrue(typeof(RePairer).GetMethod(nameof(RePairer.RunAsync))!.GetParameters().Any(p => p.ParameterType == typeof(FixConsent)));
    }

    // ---- the dialog ----

    [TestMethod]
    public void ADialogAnsweredOkIssuesAConsentAndAnythingElseIssuesNone()
    {
        var prompter = new FakePrompter();
        var dialog = new FixConsentDialog(prompter, FakeDesktop.Interactive());
        FixPlan plan = new FixPlan([], [], [], [], false, false);

        prompter.SayOk();
        Assert.IsNotNull(dialog.AskChanges(plan).Consent);
        prompter.SayCancel();
        Assert.IsNull(dialog.AskChanges(plan).Consent);
        prompter.SayTimedOut();
        Assert.IsNull(dialog.AskChanges(plan).Consent);
        Assert.IsNull(dialog.AskChanges(plan).Consent, "An empty answer queue is a cancel.");
    }

    [TestMethod]
    public void TheDialogIssuesNothingWhenNobodyIsAtTheScreenAndNeverShowsItself()
    {
        var prompter = new FakePrompter();
        prompter.SayOk();
        var dialog = new FixConsentDialog(prompter, FakeDesktop.NoOneThere());

        FixConsentAnswer answer = dialog.AskChanges(new FixPlan([], [], [], [], false, false));

        Assert.IsNull(answer.Consent);
        Assert.IsEmpty(prompter.Asked);
    }

    [TestMethod]
    public void TheTwoConsentsAreNotInterchangeable()
    {
        var prompter = new FakePrompter();
        var dialog = new FixConsentDialog(prompter, FakeDesktop.Interactive());
        prompter.SayOk();
        FixConsent? changes = dialog.AskChanges(new FixPlan([], [], [], [], false, false)).Consent;
        prompter.SayOk();
        FixConsent? rePair = dialog.AskRePair(["AirPods"], removeFirst: true);

        Assert.AreEqual(FixConsentKind.Changes, changes!.Kind);
        Assert.AreEqual(FixConsentKind.RePair, rePair!.Kind);
    }

    [TestMethod]
    public void TheDialogPageDefaultsToCancelAndHasATimeout()
    {
        var prompt = new FixPrompt("Fix connection", "Undo Earshot's changes", "These changes will be made.", ["Enable the device"], [], "Make these changes");

        TaskDialogPage page = FixDialogPages.Build(prompt, out TimeSpan timeout);

        Assert.AreSame(page.Buttons.Single(b => b == TaskDialogButton.Cancel), page.DefaultButton);
        Assert.IsTrue(page.AllowCancel);
        Assert.IsGreaterThan(TimeSpan.Zero, timeout);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(10), timeout);
        Assert.IsTrue(page.Buttons.Any(b => b.Text == "Make these changes"));
        Assert.IsNull(page.RadioButtons.FirstOrDefault(r => r.Checked), "Nothing is chosen for the owner.");
    }

    [TestMethod]
    public void ADialogWithSeveralDevicesOffersThemAsChoicesAndChoosesNone()
    {
        var prompt = new FixPrompt("Fix connection", "Which AirPods?", "Pick one.", [], ["AirPods one", "AirPods two"], "Continue");

        TaskDialogPage page = FixDialogPages.Build(prompt, out _);

        Assert.HasCount(2, page.RadioButtons);
        Assert.AreEqual("AirPods one", page.RadioButtons[0].Text);
        Assert.IsFalse(page.RadioButtons.Any(r => r.Checked));
    }

    private static readonly string[] CasePictureNames = [PromptPictures.CaseFront, PromptPictures.CaseBack];

    private static readonly string[] CasePictureCaptions = [Earshot.Pairing.PairingText.CaseFront, Earshot.Pairing.PairingText.CaseBack];

    // Fix pairs the AirPods again too, so its pairing question shows the same pictures of each kind of case as Pair AirPods'
    // first question, with the iPhone line. Before, Fix gave only two lines of text in the window during the scan.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheRePairQuestionShowsEachKindOfCaseWithItsPicture(bool removeFirst)
    {
        FixPrompt rePair = FixPromptText.RePair(["AirPods"], removeFirst);

        Assert.IsNotNull(rePair.Pictures);
        CollectionAssert.AreEqual(CasePictureNames, rePair.Pictures.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(CasePictureCaptions, rePair.Pictures.Select(p => p.Caption).ToArray());
        StringAssert.Contains(rePair.Body, Earshot.Pairing.PairingText.TurnOffIPhoneBluetooth);
        Assert.IsEmpty(rePair.Lines, "The picture window shows the body and the pictures; a list would not be shown.");
    }

    [TestMethod]
    public void ThePromptTextNamesTheDevicesAndLeavesOutIdentifiers()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        FixPlan plan = FixPlanner.Build(rig.Nodes, rig.Records, rig.Leftovers).Plan;

        FixPrompt changes = FixPromptText.Changes(plan);
        FixPrompt rePair = FixPromptText.RePair(plan.DeviceNames, removeFirst: true);

        foreach (FixPrompt prompt in new[] { changes, rePair })
        {
            string all = prompt.Title + prompt.Heading + prompt.Body + string.Join("", prompt.Lines);
            Assert.DoesNotContain(RecordedNodes.AirPodsAddress, all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BTHENUM", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EmDash, all, StringComparison.Ordinal);
        }

        Assert.Contains("Jonathan", changes.Body + string.Join("", changes.Lines), StringComparison.Ordinal);
        string second = rePair.Heading + rePair.Body + string.Join("", rePair.Lines);
        Assert.Contains("Jonathan", second, StringComparison.Ordinal);
        Assert.Contains("remove", second, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pair", second, StringComparison.OrdinalIgnoreCase);
    }
}
