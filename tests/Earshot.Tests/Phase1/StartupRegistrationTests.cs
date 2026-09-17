using System.Security;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

// Every test uses an in-memory registry. The real HKCU is never read or written here.
[TestClass]
public sealed class StartupRegistrationTests
{
    private const string ExePath = @"C:\Program Files\Earshot\Earshot.exe";
    private const string Command = "\"C:\\Program Files\\Earshot\\Earshot.exe\" --startup";

    private static readonly byte[] EnabledInWindows = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DisabledInWindows = [0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01];

    private static StartupRegistration Create(FakeStartupRegistry registry, CapturingLog log, bool safeMode = false, string? exePath = ExePath, bool redirected = false) =>
        new(registry, log, safeMode, exePath, redirected);

    [TestMethod]
    public void TheCommandQuotesThePathAndAddsTheStartupArgument()
    {
        Assert.AreEqual(Command, StartupRegistration.CommandFor(ExePath));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow(new byte[0], false)]
    [DataRow(new byte[] { 0x02, 0, 0, 0 }, false)]
    [DataRow(new byte[] { 0x03, 0, 0, 0 }, true)]
    [DataRow(new byte[] { 0x01 }, true)]
    [DataRow(new byte[] { 0x06 }, false)]
    [DataRow(new byte[] { 0x07 }, true)]
    [DataRow(new byte[] { 0x00 }, false)]
    public void BitOneOfTheFirstByteMeansTurnedOffInWindows(byte[]? data, bool expected)
    {
        Assert.AreEqual(expected, StartupRegistration.IsTurnedOffInWindows(data));
    }

    [TestMethod]
    public void ReadReportsOffOnAndTurnedOffInWindows()
    {
        var registry = new FakeStartupRegistry();
        var log = new CapturingLog();
        StartupRegistration startup = Create(registry, log);

        Assert.AreEqual(StartupState.Off, startup.Read());

        registry.Run["Earshot"] = Command;
        Assert.AreEqual(StartupState.On, startup.Read());

        registry.Approved["Earshot"] = EnabledInWindows;
        Assert.AreEqual(StartupState.On, startup.Read());

        registry.Approved["Earshot"] = DisabledInWindows;
        Assert.AreEqual(StartupState.DisabledInWindows, startup.Read());

        registry.Run.Remove("Earshot");
        Assert.AreEqual(StartupState.Off, startup.Read(), "A StartupApproved entry without a Run value starts nothing.");
    }

    [TestMethod]
    public void AnUnreadableRegistryIsUnknownAndLogged()
    {
        var registry = new FakeStartupRegistry { ReadFailure = new SecurityException("denied") };
        var log = new CapturingLog();

        Assert.AreEqual(StartupState.Unknown, Create(registry, log).Read());
        Assert.IsTrue(log.Has(LogLevel.Warn, "Open on startup could not be read"));
    }

    [TestMethod]
    public void TurningOnWritesTheQuotedCommandOnce()
    {
        var registry = new FakeStartupRegistry();
        var log = new CapturingLog();
        StartupRegistration startup = Create(registry, log);

        ControllerResult first = startup.Apply(true);
        ControllerResult second = startup.Apply(true);

        Assert.AreEqual(OpStatus.Success, first.Status);
        Assert.AreEqual(OpStatus.AlreadyInState, second.Status);
        Assert.AreEqual(1, registry.Writes);
        Assert.AreEqual(Command, registry.Run["Earshot"]);
        Assert.IsEmpty(registry.Approved, "StartupApproved is never written.");
    }

    [TestMethod]
    public void TurningOnReplacesAValueThatPointsElsewhere()
    {
        var registry = new FakeStartupRegistry();
        registry.Run["Earshot"] = "\"D:\\Old\\Earshot.exe\" --startup";

        ControllerResult result = Create(registry, new CapturingLog()).Apply(true);

        Assert.AreEqual(OpStatus.Success, result.Status);
        Assert.AreEqual(Command, registry.Run["Earshot"]);
    }

    [TestMethod]
    public void TurningOffDeletesTheValueOnlyWhenPresent()
    {
        var registry = new FakeStartupRegistry();
        StartupRegistration startup = Create(registry, new CapturingLog());

        Assert.AreEqual(OpStatus.AlreadyInState, startup.Apply(false).Status);
        Assert.AreEqual(0, registry.Deletes);

        registry.Run["Earshot"] = Command;
        Assert.AreEqual(OpStatus.Success, startup.Apply(false).Status);
        Assert.AreEqual(1, registry.Deletes);
        Assert.IsFalse(registry.Run.ContainsKey("Earshot"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SafeModeWritesNothingAndLogsInstead(bool openOnStartup)
    {
        var registry = new FakeStartupRegistry();
        registry.Run["Earshot"] = "\"D:\\Old\\Earshot.exe\" --startup";
        var log = new CapturingLog();

        ControllerResult result = Create(registry, log, safeMode: true).Apply(openOnStartup);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(StartupRegistration.SafeModeMessage, result.UserMessage);
        Assert.HasCount(1, result.Steps);
        Assert.AreEqual(NativeCodes.NotAttempted, result.Steps[0].Code);
        Assert.AreEqual(0, registry.Writes);
        Assert.AreEqual(0, registry.Deletes);
        Assert.AreEqual("\"D:\\Old\\Earshot.exe\" --startup", registry.Run["Earshot"]);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode: startup setting not changed."));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ATestDataFolderWritesNothingAndLogsInstead(bool openOnStartup)
    {
        var registry = new FakeStartupRegistry();
        registry.Run["Earshot"] = "\"D:\\Old\\Earshot.exe\" --startup";
        var log = new CapturingLog();

        ControllerResult result = Create(registry, log, redirected: true).Apply(openOnStartup);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(StartupRegistration.TestFolderMessage, result.UserMessage);
        Assert.AreEqual(0, registry.Writes);
        Assert.AreEqual(0, registry.Deletes);
        Assert.IsTrue(log.Has(LogLevel.Warn, StartupRegistration.TestFolderMessage));
    }

    [TestMethod]
    public void AnEntryTurnedOffInWindowsIsLeftForTheUser()
    {
        var registry = new FakeStartupRegistry();
        registry.Run["Earshot"] = Command;
        registry.Approved["Earshot"] = DisabledInWindows;

        ControllerResult result = Create(registry, new CapturingLog()).Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual("Turned off in Windows. Turn it on in Settings > Apps > Startup.", result.UserMessage);
        Assert.AreEqual(0, registry.Writes);
        CollectionAssert.AreEqual(DisabledInWindows, registry.Approved["Earshot"]);
    }

    [TestMethod]
    public void ALeftoverTurnedOffEntryWithoutARunValueIsNotReportedAsOn()
    {
        // Windows keeps the StartupApproved entry after the Run value is removed, and applies it again
        // once the value is back.
        var registry = new FakeStartupRegistry();
        registry.Approved["Earshot"] = DisabledInWindows;
        StartupRegistration startup = Create(registry, new CapturingLog());
        Assert.AreEqual(StartupState.Off, startup.Read());

        ControllerResult result = startup.Apply(true);

        Assert.AreEqual(OpStatus.Partial, result.Status);
        Assert.IsFalse(result.IsSuccess, "A partial result is shown on a card.");
        Assert.AreEqual(StartupRegistration.TurnedOffInWindowsMessage, result.UserMessage);
        Assert.HasCount(1, result.Steps);
        Assert.AreEqual(NativeCodes.NotAttempted, result.Steps[0].Code);
        Assert.AreEqual(1, registry.Writes, "The command is written so it is current when the user turns the entry on.");
        Assert.AreEqual(Command, registry.Run["Earshot"]);
        Assert.AreEqual(StartupState.DisabledInWindows, startup.Read());
        CollectionAssert.AreEqual(DisabledInWindows, registry.Approved["Earshot"], "StartupApproved is never written.");
    }

    [TestMethod]
    public void AStalePathTurnedOffInWindowsIsUpdatedButNotReportedAsOn()
    {
        var registry = new FakeStartupRegistry();
        registry.Run["Earshot"] = "\"D:\\Old\\Earshot.exe\" --startup";
        registry.Approved["Earshot"] = DisabledInWindows;

        ControllerResult result = Create(registry, new CapturingLog()).Apply(true);

        Assert.AreEqual(OpStatus.Partial, result.Status);
        Assert.AreEqual(Command, registry.Run["Earshot"]);
        CollectionAssert.AreEqual(DisabledInWindows, registry.Approved["Earshot"]);
    }

    [TestMethod]
    public void TheRunValueNeedsRepairWhenMissingOrStartingAnotherFile()
    {
        var registry = new FakeStartupRegistry();
        StartupRegistration startup = Create(registry, new CapturingLog());

        Assert.IsTrue(startup.RunValueNeedsRepair(), "Missing.");

        registry.Run["Earshot"] = "\"D:\\Unzipped\\Earshot\\Earshot.exe\" --startup";
        Assert.IsTrue(startup.RunValueNeedsRepair(), "Another file.");

        registry.Run["Earshot"] = Command;
        Assert.IsFalse(startup.RunValueNeedsRepair(), "This file.");

        registry.Run["Earshot"] = Command.ToUpperInvariant().Replace("--STARTUP", "--startup", StringComparison.Ordinal);
        Assert.IsFalse(startup.RunValueNeedsRepair(), "Paths differ only in case.");

        registry.Run["Earshot"] = Command.Replace("--startup", "--STARTUP", StringComparison.Ordinal);
        Assert.IsTrue(startup.RunValueNeedsRepair(), "The tray rejects any other spelling of the argument.");

        registry.Run.Remove("Earshot");
        registry.Approved["Earshot"] = DisabledInWindows;
        Assert.IsFalse(startup.RunValueNeedsRepair(), "Turned off in Windows: nothing written would start it.");
        Assert.AreEqual(0, registry.Writes, "Checking never writes.");
    }

    // Only a value in the form Earshot writes names a program to look for; anything else is never reported as gone.
    [TestMethod]
    [DataRow("\"C:\\Program Files\\Earshot\\Earshot.exe\" --startup", "C:\\Program Files\\Earshot\\Earshot.exe")]
    [DataRow("\"D:\\Unzipped\\Earshot\\Earshot.exe\" --startup", "D:\\Unzipped\\Earshot\\Earshot.exe")]
    [DataRow("C:\\Program Files\\Earshot\\Earshot.exe --startup", null)]
    [DataRow("\"C:\\Program Files\\Earshot\\Earshot.exe\"", null)]
    [DataRow("\"C:\\Program Files\\Earshot\\Earshot.exe\" --startup --other", null)]
    [DataRow("\"Earshot.exe\" --startup", null)]
    [DataRow("\"\" --startup", null)]
    [DataRow("\"C:\\Program Files\\Earshot\\Earshot.exe --startup", null)]
    [DataRow("", null)]
    public void TheRunValueTargetIsTheQuotedFullPathOfACommandInEarshotsForm(string? command, string? target) =>
        Assert.AreEqual(target, StartupRegistration.TargetOf(command));

    [TestMethod]
    public void ARunValueIsReportedMissingOnlyWhenItsProgramIsGone()
    {
        var registry = new FakeStartupRegistry();
        bool there = true;
        var startup = new StartupRegistration(registry, new CapturingLog(), safeMode: false, ExePath, fileExists: _ => there);

        Assert.IsNull(StartupRegistration.TargetOf(null));
        Assert.IsFalse(startup.RunValueTargetMissing(), "No value.");
        registry.Run["Earshot"] = Command;
        Assert.IsFalse(startup.RunValueTargetMissing(), "The program is there.");
        there = false;
        Assert.IsTrue(startup.RunValueTargetMissing());
        registry.Run["Earshot"] = "not a command Earshot writes";
        Assert.IsFalse(startup.RunValueTargetMissing());
        Assert.IsFalse(new StartupRegistration(new FakeStartupRegistry { ReadFailure = new SecurityException("denied") }, new CapturingLog(), false, ExePath,
            fileExists: _ => false).RunValueTargetMissing(), "A value that could not be read is not reported as gone.");
    }

    [TestMethod]
    public void TheRepairCheckLogsAndDeclinesWhenItCannotRead()
    {
        var log = new CapturingLog();
        var unreadable = new FakeStartupRegistry { ReadFailure = new SecurityException("denied") };

        Assert.IsFalse(Create(unreadable, log).RunValueNeedsRepair());
        Assert.IsTrue(log.Has(LogLevel.Warn, "Open on startup could not be checked"));

        Assert.IsFalse(Create(new FakeStartupRegistry(), log, exePath: null).RunValueNeedsRepair());
        Assert.IsTrue(log.Has(LogLevel.Warn, "was not checked"));
    }

    [TestMethod]
    public void ACommandLongerThan260CharactersIsRefused()
    {
        var registry = new FakeStartupRegistry();
        string longPath = @"C:\" + new string('x', 250) + @"\Earshot.exe";

        ControllerResult result = Create(registry, new CapturingLog(), exePath: longPath).Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(StartupRegistration.PathTooLongMessage, result.UserMessage);
        Assert.AreEqual(0, registry.Writes);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void WithoutAnExePathNothingIsWritten(string? exePath)
    {
        var registry = new FakeStartupRegistry();

        ControllerResult result = Create(registry, new CapturingLog(), exePath: exePath).Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(0, registry.Writes);
    }

    [TestMethod]
    public void AWriteFailureIsReportedWithItsCodeAndLogged()
    {
        var denied = new UnauthorizedAccessException("Access to the registry key is denied.");
        var registry = new FakeStartupRegistry { WriteFailure = denied };
        var log = new CapturingLog();

        ControllerResult result = Create(registry, log).Apply(true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(StartupRegistration.ChangeFailedMessage, result.UserMessage);
        Assert.HasCount(1, result.Steps);
        Assert.IsFalse(result.Steps[0].Ok);
        Assert.AreEqual(denied.HResult, result.Steps[0].Code);
        Assert.AreEqual(NativeCodes.Name(denied.HResult), result.Steps[0].CodeName);
        Assert.IsTrue(log.Has(LogLevel.Error, "Open on startup could not be changed."));
    }

    [TestMethod]
    public void TheRegistryAbstractionCannotWriteStartupApproved()
    {
        string[] writers = typeof(IStartupRegistry).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Approved", StringComparison.Ordinal) && !n.StartsWith("Read", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(writers);
    }
}
