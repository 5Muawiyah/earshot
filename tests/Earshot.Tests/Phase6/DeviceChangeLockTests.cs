using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// The device change lock over a temporary folder. Nothing else is locked and nothing waits in real time.
// The tests run unelevated, so the lock is taken with the access a fake node table gets (the machine's plus
// the current user); the machine access list itself is checked as data.
[TestClass]
public sealed class DeviceChangeLockTests
{
    private static readonly DeviceChangeLockAccess Access = DeviceChangeLockAccess.WithCurrentUser();

    private static bool NoWait(TimeSpan _) => true;

    private static FileSecurity ReadSecurity(string folder) =>
        new FileInfo(Path.Combine(folder, DeviceChangeLock.FileName)).GetAccessControl(AccessControlSections.Access);

    private static bool CanOpenForWrite(string folder)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(folder, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [TestMethod]
    public void TheLockIsHeldUntilDisposed()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, steps);

        Assert.IsNotNull(held);
        Assert.IsTrue(steps.Single().Ok);
        Assert.IsFalse(CanOpenForWrite(temp.Path), "Another writer is kept out.");
        held.Dispose();
        Assert.IsTrue(CanOpenForWrite(temp.Path));
    }

    [TestMethod]
    public void TheSameThreadCanTakeItAgainAndOnlyTheOutermostReleaseFreesIt()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        using DeviceChangeLock? outer = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, steps);
        DeviceChangeLock? inner = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, steps);
        Assert.IsNotNull(outer);
        Assert.IsNotNull(inner);
        inner.Dispose();

        Assert.IsFalse(CanOpenForWrite(temp.Path), "Still held by the outer caller.");
        Assert.HasCount(1, steps, "Taking it again opens nothing.");
        outer.Dispose();
        Assert.IsTrue(CanOpenForWrite(temp.Path));
    }

    [TestMethod]
    public async Task AnotherThreadWaitsAndGivesUpWithABusyStep()
    {
        using var temp = new TempFolder();
        using DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, new List<StepOutcome>());
        Assert.IsNotNull(held);
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? other = await Task.Factory.StartNew(
            () => DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.FromMilliseconds(500), _ => ++waits > 0, steps),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.IsNull(other);
        Assert.AreEqual(2, waits);
        StepOutcome busy = steps.Single();
        Assert.IsTrue(DeviceChangeLock.IsBusy(busy));
        Assert.AreEqual("ERROR_SHARING_VIOLATION", busy.CodeName);
        Assert.Contains("still running after 0.5 s", busy.Detail!);
    }

    [TestMethod]
    public void AWaitThatStopsGivesUpAtOnce()
    {
        using var temp = new TempFolder();
        using var other = new FileStream(Path.Combine(temp.Path, DeviceChangeLock.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.FromMinutes(5), _ => ++waits < 0, steps);

        Assert.IsNull(held);
        Assert.AreEqual(1, waits);
        Assert.IsTrue(DeviceChangeLock.IsBusy(steps.Single()));
        Assert.Contains("stopped waiting", steps.Single().Detail!);
    }

    [TestMethod]
    public void AnOpenThatFailsForAnotherReasonIsRecordedAndNotRetried()
    {
        using var temp = new TempFolder();
        string missing = Path.Combine(temp.Path, "missing");
        var steps = new List<StepOutcome>();
        int waits = 0;

        DeviceChangeLock? held = DeviceChangeLock.TryAcquire(missing, Access, TimeSpan.FromMinutes(5), _ => ++waits > 0, steps);

        Assert.IsNull(held);
        Assert.AreEqual(0, waits);
        StepOutcome failed = steps.Single();
        Assert.IsFalse(failed.Ok);
        Assert.IsFalse(DeviceChangeLock.IsBusy(failed), "A missing folder is not another change holding the lock.");
        Assert.AreEqual("ERROR_PATH_NOT_FOUND", failed.CodeName);
    }

    [TestMethod]
    public void ANewLockFileGetsOnlyTheHoldersAccessList()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        using (DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, steps))
        {
            Assert.IsNotNull(held);
        }

        Assert.HasCount(1, steps, "A file created with the access list needs no reset.");
        FileSecurity security = ReadSecurity(temp.Path);
        Assert.IsNull(Access.Problem(security));
        Assert.IsTrue(security.AreAccessRulesProtected, "Nothing is taken from the folder.");
        var sids = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();
        Assert.DoesNotContain(Sddl.UsersSid, sids);
        Assert.DoesNotContain("S-1-5-11", sids, "Authenticated Users get nothing.");
        Assert.DoesNotContain("S-1-1-0", sids, "Everyone gets nothing.");
    }

    [TestMethod]
    public void AnExistingLockFileWithAnotherAccessListIsResetOnOpen()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(Path.Combine(temp.Path, DeviceChangeLock.FileName), []);
        Assert.IsNotNull(Access.Problem(ReadSecurity(temp.Path)), "A plain file takes the folder's entries.");
        var steps = new List<StepOutcome>();

        using (DeviceChangeLock? held = DeviceChangeLock.TryAcquire(temp.Path, Access, TimeSpan.Zero, NoWait, steps))
        {
            Assert.IsNotNull(held);
        }

        Assert.IsTrue(steps.Single(s => s.Step == DeviceChangeLock.StepName).Ok);
        StepOutcome reset = steps.Single(s => s.Step == DeviceChangeLock.AccessStepName);
        Assert.IsTrue(reset.Ok, reset.Detail);
        Assert.Contains("was reset", reset.Detail!);
        Assert.IsNull(Access.Problem(ReadSecurity(temp.Path)));
    }

    [TestMethod]
    public void TheMachineAccessListGrantsOnlySystemAndAdministratorsAndOwnerRightsReadControl()
    {
        DeviceChangeLockAccess machine = DeviceChangeLockAccess.Machine;

        CollectionAssert.AreEquivalent(new[] { Sddl.LocalSystemSid, Sddl.AdministratorsSid }, machine.Holders.Select(h => h.Value).ToArray());
        var sd = new RawSecurityDescriptor(machine.Sddl);
        Assert.AreNotEqual(ControlFlags.None, sd.ControlFlags & ControlFlags.DiscretionaryAclProtected);
        var aces = sd.DiscretionaryAcl!.Cast<CommonAce>().ToDictionary(a => a.SecurityIdentifier.Value, a => a.AccessMask);
        Assert.HasCount(3, aces);
        Assert.AreEqual(0x001F01FF, aces[Sddl.LocalSystemSid]);
        Assert.AreEqual(0x001F01FF, aces[Sddl.AdministratorsSid]);
        Assert.AreEqual(0x00020000, aces[DeviceChangeLockAccess.OwnerRightsSid], "The owner gets read control only, never WRITE_DAC.");
        Assert.IsNull(machine.Problem(machine.CreateSecurity()));
    }

    [TestMethod]
    public void AnAccessListWithAnotherEntryIsAProblem()
    {
        DeviceChangeLockAccess machine = DeviceChangeLockAccess.Machine;
        string users = "(A;;0x1200a9;;;" + Sddl.UsersSid + ")";

        Assert.IsNotNull(machine.Problem(Security(machine.Sddl + users)));
        Assert.IsNotNull(machine.Problem(Security(machine.Sddl.Replace("D:P", "D:", StringComparison.Ordinal))), "Not protected.");
        Assert.IsNotNull(machine.Problem(Security("D:P(A;;FA;;;SY)(A;;FA;;;BA)")), "No OWNER RIGHTS entry.");
        Assert.IsNotNull(machine.Problem(Security("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;OW)")), "OWNER RIGHTS with more than read control.");
        Assert.IsNotNull(Access.Problem(machine.CreateSecurity()), "The test access list also names the current user.");
    }

    [TestMethod]
    public void OnlyTheRealNodeApiGetsTheMachineAccessList()
    {
        Assert.AreSame(DeviceChangeLockAccess.Machine, DeviceChangeLockAccess.For(new CfgMgr32NodeApi()));

        DeviceChangeLockAccess fake = DeviceChangeLockAccess.For(RecordedNodes.Table());
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.Contains(me.User!, fake.Holders);
    }

    [TestMethod]
    public void TheNodeChangeCallTakesTheLockForItsNodeApi()
    {
        using var temp = new TempFolder();
        var steps = new List<StepOutcome>();

        using (DeviceChangeLock? held = DeviceChangeLock.TryAcquireForNodeChange(temp.Path, RecordedNodes.Table(), steps))
        {
            Assert.IsNotNull(held);
            Assert.IsFalse(CanOpenForWrite(temp.Path));
        }

        Assert.IsTrue(steps.Single().Ok);
        Assert.IsNull(Access.Problem(ReadSecurity(temp.Path)));
    }

    [TestMethod]
    public void TheNodeChangeWaitLeavesTheGateTaskTimeForTheChange()
    {
        TimeSpan gateLimit = XmlConvert.ToTimeSpan(TaskPlan.GateTimeLimit);

        Assert.IsLessThanOrEqualTo(gateLimit / 2, DeviceChangeLock.NodeChangeLockTimeout);
        Assert.IsGreaterThan(TimeSpan.Zero, DeviceChangeLock.NodeChangeLockTimeout);
    }

    private static FileSecurity Security(string sddl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        return security;
    }

    [TestMethod]
    public void OnlyAFailedLockStepIsBusy()
    {
        Assert.IsFalse(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32, ok: true)));
        Assert.IsFalse(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32("write-protection-intent", 32)));
        Assert.IsTrue(DeviceChangeLock.IsBusy(StepOutcomes.FromWin32(DeviceChangeLock.StepName, 32)));
    }
}
