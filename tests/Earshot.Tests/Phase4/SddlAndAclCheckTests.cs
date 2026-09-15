using Earshot.Boot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class SddlAndAclCheckTests
{
    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherUser = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    [TestMethod]
    public void BuildersProduceTheDesignStrings()
    {
        Assert.AreEqual("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FR;;;" + User + ")", Sddl.TaskFolder(User));
        Assert.AreEqual("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;" + User + ")", Sddl.RunnableTask(User));
        Assert.AreEqual("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;" + User + ")", Sddl.ReadableTask(User));

        var machine = new System.Security.AccessControl.RawSecurityDescriptor(Sddl.MachineFolder);
        Assert.AreEqual(Sddl.AdministratorsSid, machine.Owner?.Value);
        Assert.AreNotEqual(0, (int)(machine.ControlFlags & System.Security.AccessControl.ControlFlags.DiscretionaryAclProtected));
        Assert.IsNotNull(machine.DiscretionaryAcl);
        string[] aces = machine.DiscretionaryAcl.Cast<System.Security.AccessControl.CommonAce>()
            .Select(a => a.SecurityIdentifier.Value + "=" + ((uint)a.AccessMask).ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        CollectionAssert.AreEqual(new[] { Sddl.LocalSystemSid + "=001F01FF", Sddl.AdministratorsSid + "=001F01FF", Sddl.UsersSid + "=001200A9" }, aces);
    }

    [TestMethod]
    [DataRow("S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [DataRow("S-1-5-21-1-2-3-500")]
    [DataRow("S-1-5-21-4294967295-4294967295-4294967295-4294967295")]
    [DataRow("S-1-12-1-1234567890-1234567890-1234567890-1234567890")]
    public void AcceptsUserSids(string sid)
    {
        Assert.IsTrue(Sddl.IsUserSid(sid));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("S-1-5-18")]
    [DataRow("S-1-5-32-544")]
    [DataRow("S-1-5-32-545")]
    [DataRow("S-1-5-11")]
    [DataRow("S-1-1-0")]
    [DataRow("SY")]
    [DataRow("BA")]
    [DataRow("S-1-5-21-1-2-3")]
    [DataRow("S-1-5-21-1-2-3-4-5")]
    [DataRow("S-1-5-21-1-2-3-4294967296")]
    [DataRow("S-1-5-21-01-2-3-1001")]
    [DataRow("S-1-5-21-1-2-3-1001)(A;;FA;;;WD")]
    [DataRow("S-1-5-21-1-2-3-1001 ")]
    [DataRow("s-1-5-21-1-2-3-1001")]
    [DataRow("S-1-5-21-1-2--1001")]
    [DataRow("S-1-5-21-١-2-3-1001")]
    public void RejectsAnythingElse(string? sid)
    {
        Assert.IsFalse(Sddl.IsUserSid(sid));
    }

    [TestMethod]
    public void BuildersRefuseANonUserSid()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Sddl.RunnableTask("S-1-1-0"));
        Assert.ThrowsExactly<ArgumentException>(() => Sddl.TaskFolder("S-1-5-21-1-2-3-1001)(A;;FA;;;WD"));
    }

    [TestMethod]
    public void TheDesignDescriptorsPassTheirOwnChecks()
    {
        Assert.IsEmpty(AclCheck.CheckMachineFolder(Sddl.MachineFolder));
        Assert.IsEmpty(AclCheck.CheckTaskFolder(Sddl.TaskFolder(User), User));
        Assert.IsEmpty(AclCheck.CheckTask(Sddl.RunnableTask(User), User, userMayRun: true));
        Assert.IsEmpty(AclCheck.CheckTask(Sddl.ReadableTask(User), User, userMayRun: false));
        Assert.IsEmpty(AclCheck.CheckTask(Sddl.RunnableTask(User), User, userMayRun: false));
    }

    [TestMethod]
    public void TheRunnableTaskGrantsTheUserExactlyFrfx()
    {
        Assert.AreEqual(0x001200A9u, AclCheck.UserAllowedMask(Sddl.RunnableTask(User), User));
        Assert.AreEqual(0x00120089u, AclCheck.UserAllowedMask(Sddl.ReadableTask(User), User));
        Assert.AreEqual(0u, AclCheck.UserAllowedMask(Sddl.RunnableTask(User), OtherUser));
    }

    [TestMethod]
    public void AReadOnlyTaskCannotBeStartedByTheUser()
    {
        IReadOnlyList<string> problems = AclCheck.CheckTask(Sddl.ReadableTask(User), User, userMayRun: true);

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "cannot start");
    }

    [TestMethod]
    [DataRow("0x1200a9", true)]
    [DataRow("FR", true)]
    [DataRow("FX", true)]
    [DataRow("GR", true)]
    [DataRow("0x1200ab", false)]    // FILE_WRITE_DATA
    [DataRow("0x1200ad", false)]    // FILE_APPEND_DATA
    [DataRow("0x1300a9", false)]    // DELETE
    [DataRow("0x1600a9", false)]    // WRITE_DAC
    [DataRow("0x1a00a9", false)]    // WRITE_OWNER
    [DataRow("FA", false)]
    [DataRow("FW", false)]
    [DataRow("GA", false)]
    [DataRow("GW", false)]
    [DataRow("WD", false)]
    [DataRow("WO", false)]
    [DataRow("SD", false)]
    [DataRow("0x1201a9", false)]    // FILE_WRITE_ATTRIBUTES
    [DataRow("0x1201e9", false)]    // FILE_DELETE_CHILD
    public void TaskUserMaskMustStayWithinFrfx(string rights, bool ok)
    {
        string sddl = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;" + rights + ";;;" + User + ")";

        IReadOnlyList<string> problems = AclCheck.CheckTask(sddl, User, userMayRun: false);

        Assert.AreEqual(ok, problems.Count == 0, string.Join(" | ", problems));
    }

    [TestMethod]
    [DataRow("S-1-1-0")]            // Everyone
    [DataRow("S-1-5-11")]           // Authenticated Users
    [DataRow("S-1-5-32-545")]       // Users
    [DataRow("S-1-3-0")]            // CREATOR OWNER
    [DataRow(OtherUser)]
    public void ANonAdminSidWithWriteFailsEveryCheck(string sid)
    {
        string task = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;" + User + ")(A;;FW;;;" + sid + ")";
        string folder = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FR;;;" + User + ")(A;CI;0x2;;;" + sid + ")";
        string machine = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICIIO;0x4;;;" + sid + ")";

        Assert.IsNotEmpty(AclCheck.CheckTask(task, User, userMayRun: true));
        Assert.IsNotEmpty(AclCheck.CheckTaskFolder(folder, User));
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder(machine));
    }

    [TestMethod]
    public void AReadOnlyAceForAnotherSidIsStillUnexpectedOnTasks()
    {
        string sddl = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;" + User + ")(A;;FR;;;AU)";

        IReadOnlyList<string> problems = AclCheck.CheckTask(sddl, User, userMayRun: true);

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "S-1-5-11");
    }

    [TestMethod]
    public void TheOwnerMustBeSystemOrAdministrators()
    {
        Assert.IsEmpty(AclCheck.CheckTask("O:SYG:SYD:P(A;;FA;;;SY)(A;;0x1200a9;;;" + User + ")", User, userMayRun: true));
        Assert.IsNotEmpty(AclCheck.CheckTask("O:" + User + "G:SYD:P(A;;FA;;;SY)(A;;0x1200a9;;;" + User + ")", User, userMayRun: true));
        Assert.IsNotEmpty(AclCheck.CheckTask("G:SYD:P(A;;FA;;;SY)(A;;0x1200a9;;;" + User + ")", User, userMayRun: true));
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder("O:BUG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
    }

    [TestMethod]
    public void TheProgramDataDefaultInheritanceIsRejected()
    {
        // ProgramData grants Users (CI)(WD,AD,WEA,WA) and CREATOR OWNER full control on children.
        string inherited = "O:" + User + "G:SYD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIIOID;GA;;;CO)" +
                           "(A;OICIID;0x1200a9;;;BU)(A;CIID;0x116;;;BU)";

        IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(inherited);

        Assert.IsTrue(problems.Any(p => p.Contains("owner", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("not protected", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(p => p.StartsWith("S-1-5-32-545 can write", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(p => p.StartsWith("S-1-3-0 can write", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MachineFolderGrantsUsersNoMoreThanReadExecute()
    {
        Assert.IsEmpty(AclCheck.CheckMachineFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FR;;;BU)"));
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)"));
    }

    [TestMethod]
    public void TheProgramFilesInheritancePassesTheInstallFolderCheck()
    {
        string programFiles = "O:BAG:SYD:AI(A;ID;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)" +
                              "(A;CIIOID;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)" +
                              "(A;ID;0x1301bf;;;SY)(A;OICIIOID;GA;;;SY)(A;ID;0x1301bf;;;BA)(A;OICIIOID;GA;;;BA)" +
                              "(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)" +
                              "(A;ID;0x1200a9;;;AC)(A;OICIIOID;GXGR;;;AC)";

        Assert.IsEmpty(AclCheck.CheckInstallFolder(programFiles), string.Join(" | ", AclCheck.CheckInstallFolder(programFiles)));
        Assert.IsNotEmpty(AclCheck.CheckInstallFolder(programFiles + "(A;OICI;0x116;;;BU)"));
        Assert.IsNotEmpty(AclCheck.CheckInstallFolder(programFiles + "(A;OICI;GA;;;CO)"), "CREATOR OWNER is allowed only inherit-only.");
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder(programFiles), "The machine folder check has no such allowance.");
    }

    [TestMethod]
    public void DenyAcesDoNotCountAgainstTheDescriptor()
    {
        string sddl = Sddl.RunnableTask(User) + "(D;;FA;;;WD)";

        Assert.IsEmpty(AclCheck.CheckTask(sddl, User, userMayRun: true));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not sddl")]
    [DataRow("O:BAG:SYD:(A;;FA;;;NOPE)")]
    public void AnUnreadableDescriptorFailsClosed(string? sddl)
    {
        Assert.IsNotEmpty(AclCheck.CheckTask(sddl, User, userMayRun: true));
        Assert.IsNotEmpty(AclCheck.CheckTaskFolder(sddl, User));
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder(sddl));
        Assert.IsNotEmpty(AclCheck.CheckInstallFolder(sddl));
    }

    [TestMethod]
    public void ANullDaclFailsClosed()
    {
        Assert.IsNotEmpty(AclCheck.CheckTask("O:BAG:SY", User, userMayRun: false));
        Assert.IsNotEmpty(AclCheck.CheckMachineFolder("O:BAG:SYD:NO_ACCESS_CONTROL"));
    }

    [TestMethod]
    public void AnEmptyProtectedDaclOnATaskOnlyFailsTheRunRule()
    {
        Assert.IsEmpty(AclCheck.CheckTask("O:BAG:SYD:P", User, userMayRun: false));
        Assert.HasCount(1, AclCheck.CheckTask("O:BAG:SYD:P", User, userMayRun: true));
    }

    [TestMethod]
    public void AnInheritOnlyUserAceDoesNotLetTheUserRunTheTask()
    {
        string sddl = "O:BAG:SYD:P(A;;FA;;;SY)(A;OICIIO;0x1200a9;;;" + User + ")";

        Assert.HasCount(1, AclCheck.CheckTask(sddl, User, userMayRun: true));
    }

    [TestMethod]
    public void GenericRightsAreMappedBeforeTheyAreJudged()
    {
        Assert.AreEqual(0x00120089u, AclCheck.MapGeneric(0x80000000));
        Assert.AreEqual(0x001F01FFu, AclCheck.MapGeneric(0x10000000));
        Assert.AreEqual(0x001200A9u, AclCheck.MapGeneric(0xA0000000));
        Assert.AreNotEqual(0u, AclCheck.MapGeneric(0x40000000) & AclCheck.WriteLikeRights);
    }
}
