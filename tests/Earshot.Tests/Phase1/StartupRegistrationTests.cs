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

    private sealed class FakeRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public Exception? ReadFailure { get; set; }

        public Exception? WriteFailure { get; set; }

        public string? ReadRunValue(string name)
        {
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Run.TryGetValue(name, out string? value) ? value : null;
        }

        public byte[]? ReadStartupApproved(string name)
        {
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Approved.TryGetValue(name, out byte[]? value) ? value : null;
        }

        public void WriteRunValue(string name, string command)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Writes++;
            Run[name] = command;
        }

        public void DeleteRunValue(string name)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Deletes++;
            Run.Remove(name);
        }
    }

    private static StartupRegistration Create(FakeRegistry registry, CapturingLog log, bool safeMode = false, string? exePath = ExePath) =>
        new(registry, log, safeMode, exePath);

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
        var registry = new FakeRegistry();
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
        var registry = new FakeRegistry { ReadFailure = new SecurityException("denied") };
        var log = new CapturingLog();

        Assert.AreEqual(StartupState.Unknown, Create(registry, log).Read());
        Assert.IsTrue(log.Has(LogLevel.Warn, "Open on startup could not be read"));
    }

    [TestMethod]
    public void TurningOnWritesTheQuotedCommandOnce()
    {
        var registry = new FakeRegistry();
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
        var registry = new FakeRegistry();
        registry.Run["Earshot"] = "\"D:\\Old\\Earshot.exe\" --startup";

        ControllerResult result = Create(registry, new CapturingLog()).Apply(true);

        Assert.AreEqual(OpStatus.Success, result.Status);
        Assert.AreEqual(Command, registry.Run["Earshot"]);
    }

    [TestMethod]
    public void TurningOffDeletesTheValueOnlyWhenPresent()
    {
        var registry = new FakeRegistry();
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
        var registry = new FakeRegistry();
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
    public void AnEntryTurnedOffInWindowsIsLeftForTheUser()
    {
        var registry = new FakeRegistry();
        registry.Run["Earshot"] = Command;
        registry.Approved["Earshot"] = DisabledInWindows;

        ControllerResult result = Create(registry, new CapturingLog()).Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual("Turned off in Windows. Turn it on in Settings > Apps > Startup.", result.UserMessage);
        Assert.AreEqual(0, registry.Writes);
        CollectionAssert.AreEqual(DisabledInWindows, registry.Approved["Earshot"]);
    }

    [TestMethod]
    public void ACommandLongerThan260CharactersIsRefused()
    {
        var registry = new FakeRegistry();
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
        var registry = new FakeRegistry();

        ControllerResult result = Create(registry, new CapturingLog(), exePath: exePath).Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(0, registry.Writes);
    }

    [TestMethod]
    public void AWriteFailureIsReportedWithItsCodeAndLogged()
    {
        var denied = new UnauthorizedAccessException("Access to the registry key is denied.");
        var registry = new FakeRegistry { WriteFailure = denied };
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
