using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Earshot.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Setup;

// The parts Setup is made of: the payload check, the zip-slip guard, the staging folder's security, the launcher, the native
// structures and the files that make the built program elevated and limit where it loads code from.
[TestClass]
public sealed class SetupPartsTests
{
    // ---- the payload check ----

    [TestMethod]
    public void AnIntactPayloadPassesAndATamperedOneIsDamaged()
    {
        byte[] zip = ReleaseZip.Build("1.5.0");
        Assert.AreEqual(PayloadVerdict.Intact, PayloadCheck.Verify(new FakePayload(zip, "1.5.0")).Verdict);

        byte[] tampered = (byte[])zip.Clone();
        tampered[10] ^= 0x40;
        Assert.AreEqual(PayloadVerdict.Damaged, PayloadCheck.Verify(new FakePayload(tampered, "1.5.0", recordedHash: ReleaseZip.Sha256(zip))).Verdict);
    }

    [TestMethod]
    public void ARecordedHashThatIsNotAHashMeansNoPayload()
    {
        byte[] zip = ReleaseZip.Build("1.5.0");
        Assert.AreEqual(PayloadVerdict.NoPayload, PayloadCheck.Verify(new FakePayload(zip, "1.5.0", recordedHash: "")).Verdict);
        Assert.AreEqual(PayloadVerdict.NoPayload, PayloadCheck.Verify(new FakePayload(zip, "1.5.0", recordedHash: "xyz")).Verdict);
    }

    [TestMethod]
    public void AnOrdinaryBuildCarriesNoPayloadAndOpeningItFails()
    {
        var payload = new EmbeddedPayload();

        Assert.IsFalse(payload.Embedded, "The tests build Setup with no release in it.");
        Assert.AreEqual("", payload.Sha256);
        Assert.ThrowsExactly<InvalidOperationException>(() => payload.Open());
    }

    [TestMethod]
    public void VersionsAreThreePartsOrNothing()
    {
        Assert.IsNotNull(PayloadCheck.ParseVersion("1.5.0"));
        Assert.IsNull(PayloadCheck.ParseVersion("1.5"));
        Assert.IsNull(PayloadCheck.ParseVersion("1.5.0.1"));
        Assert.IsNull(PayloadCheck.ParseVersion(""));
        Assert.IsNull(PayloadCheck.ParseVersion("one"));
    }

    // ---- the zip-slip guard ----

    private static string Extract(byte[] zip, out string target, TempFolder temp)
    {
        target = temp.File("app");
        using var stream = new MemoryStream(zip);
        ZipExtractor.Extract(stream, target);
        return target;
    }

    [TestMethod]
    public void AValidReleaseIsUnpackedAndMatchesItsFileList()
    {
        using var temp = new TempFolder();

        string target = Extract(ReleaseZip.Build("1.5.0"), out _, temp);

        Assert.IsTrue(File.Exists(Path.Combine(target, "Earshot.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "runtimes", "native.txt")));
        ReleaseFileList.Verify(target);
    }

    [TestMethod]
    [DataRow("Earshot/../evil.txt")]
    [DataRow("Earshot/a/../../evil.txt")]
    [DataRow("Earshot/..\\evil.txt")]
    [DataRow("Earshot/C:/evil.txt")]
    [DataRow("Earshot/evil.txt:stream")]
    [DataRow("Earshot//evil.txt")]
    [DataRow("Earshot/a./evil.txt")]
    [DataRow("Earshot/a /evil.txt")]
    [DataRow("Earshot/con\u0001.txt")]
    public void AnEntryThatWouldLeaveTheTargetIsRefusedAndNothingIsWrittenOutside(string name)
    {
        using var temp = new TempFolder();
        byte[] list = Encoding.UTF8.GetBytes("{\"Files\":[]}");
        byte[] zip = ReleaseZip.FromEntries(("Earshot/Earshot.files.json", list), (name, [1, 2, 3]));

        Assert.ThrowsExactly<PayloadException>(() => Extract(zip, out _, temp));

        Assert.IsFalse(File.Exists(temp.File("evil.txt")), "Nothing landed next to the target.");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "evil.txt")));
    }

    [TestMethod]
    public void AnEntryOutsideTheReleaseFolderIsRefused()
    {
        using var temp = new TempFolder();
        byte[] zip = ReleaseZip.FromEntries(("Earshot/Earshot.files.json", Encoding.UTF8.GetBytes("{}")), ("Other/x.txt", [1]));

        Assert.ThrowsExactly<PayloadException>(() => Extract(zip, out _, temp));
    }

    [TestMethod]
    public void AZipWithNoFileListOrTwoOfThemIsRefused()
    {
        using var temp = new TempFolder();

        Assert.ThrowsExactly<PayloadException>(() => Extract(ReleaseZip.FromEntries(("Earshot/Earshot.exe", [1])), out _, temp));
        Assert.ThrowsExactly<PayloadException>(() => Extract(
            ReleaseZip.FromEntries(("Earshot/Earshot.files.json", [2]), ("Earshot.files.json", [1])), out _, new TempFolder()));
    }

    [TestMethod]
    public void ANameThatRepeatsOnlyDifferingInCaseIsRefused()
    {
        using var temp = new TempFolder();
        byte[] zip = ReleaseZip.FromEntries(
            ("Earshot/Earshot.files.json", Encoding.UTF8.GetBytes("{}")), ("Earshot/A.dll", [1]), ("Earshot/a.dll", [2]));

        Assert.ThrowsExactly<PayloadException>(() => Extract(zip, out _, temp));
    }

    [TestMethod]
    public void ExtractionNeverWritesOverAFileThatIsAlreadyThere()
    {
        using var temp = new TempFolder();
        string target = temp.File("app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Earshot.exe"), "planted");

        using var stream = new MemoryStream(ReleaseZip.Build("1.5.0"));
        Assert.ThrowsExactly<IOException>(() => ZipExtractor.Extract(stream, target));

        Assert.AreEqual("planted", File.ReadAllText(Path.Combine(target, "Earshot.exe")));
    }

    [TestMethod]
    public void AnUnpackedFileThatDiffersFromTheListOrAnExtraFileIsCaught()
    {
        using var temp = new TempFolder();
        string target = Extract(ReleaseZip.Build("1.5.0"), out _, temp);
        File.WriteAllText(Path.Combine(target, "extra.dll"), "x");

        PayloadException extra = Assert.ThrowsExactly<PayloadException>(() => ReleaseFileList.Verify(target));
        StringAssert.Contains(extra.Message, "extra.dll");

        File.Delete(Path.Combine(target, "extra.dll"));
        File.WriteAllText(Path.Combine(target, "Earshot.dll"), "changed");
        PayloadException changed = Assert.ThrowsExactly<PayloadException>(() => ReleaseFileList.Verify(target));
        StringAssert.Contains(changed.Message, "Earshot.dll");
    }

    // ---- the staging folder's security ----

    [TestMethod]
    public void TheDescriptorSetupAsksForPassesItsOwnCheckAndOneThatLetsUsersInDoesNot()
    {
        Assert.IsEmpty(StagingAcl.Check(StagingAcl.AdminOnlySddl, strict: true));
        Assert.IsEmpty(StagingAcl.Check(StagingAcl.MachineFolderSddl, strict: false));
        Assert.IsNotEmpty(StagingAcl.Check(StagingAcl.MachineFolderSddl, strict: true), "Users may read the Earshot folder but not the staging folders.");
        Assert.IsNotEmpty(StagingAcl.Check("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)", strict: false));
        Assert.IsNotEmpty(StagingAcl.Check("O:BAG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", strict: true), "A DACL that is not protected inherits from the folder above.");
        Assert.IsNotEmpty(StagingAcl.Check("O:BUG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", strict: true), "The owner must be SYSTEM or Administrators.");
        Assert.IsNotEmpty(StagingAcl.Check(null, strict: true));
        Assert.IsNotEmpty(StagingAcl.Check("not sddl", strict: true));
    }

    [TestMethod]
    public void EveryoneWithWriteAccessToTheEarshotFolderIsRefusedEvenWhenItIsNotFullControl()
    {
        // FILE_WRITE_DATA for Authenticated Users.
        Assert.IsNotEmpty(StagingAcl.Check("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x2;;;AU)", strict: false));
        // Read and execute is allowed there.
        Assert.IsEmpty(StagingAcl.Check("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)", strict: false));
    }

    [TestMethod]
    public void StagingMakesEachLevelReadsItBackAndUsesAFreshGuidFolder()
    {
        using var temp = new TempFolder();
        var acl = new FakeAcl();
        string machine = temp.File("Earshot");

        StagingResult result = new StagingFolder(acl, machine, () => "0123456789abcdef0123456789abcdef").Create();

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.AreEqual(Path.Combine(machine, "setup-staging", "0123456789abcdef0123456789abcdef"), result.Path);
        Assert.HasCount(3, acl.Created);
        Assert.EndsWith("|" + StagingAcl.MachineFolderSddl, acl.Created[0]);
        Assert.EndsWith("|" + StagingAcl.AdminOnlySddl, acl.Created[1]);
        Assert.EndsWith("|" + StagingAcl.AdminOnlySddl, acl.Created[2]);
        Assert.HasCount(3, acl.ReadPaths);
    }

    [TestMethod]
    public void AStagingFolderSomeoneElseMadeFirstIsNeverUsedOrDeleted()
    {
        using var temp = new TempFolder();
        string machine = temp.File("Earshot");
        string planted = Path.Combine(machine, "setup-staging");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "keep.txt"), "theirs");
        var acl = new FakeAcl
        {
            ReadAs = path => path == planted ? "O:BUG:BUD:(A;OICI;FA;;;BU)" : StagingAcl.MachineFolderSddl,
        };

        StagingResult result = new StagingFolder(acl, machine).Create();

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Detail, "Remove it by hand");
        Assert.IsTrue(File.Exists(Path.Combine(planted, "keep.txt")), "It is left where it is.");
    }

    [TestMethod]
    public void AnUnreadableDescriptorIsNotTrusted()
    {
        using var temp = new TempFolder();
        var acl = new FakeAcl { ReadAs = _ => null };

        StagingResult result = new StagingFolder(acl, temp.File("Earshot")).Create();

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Detail, "could not be read");
    }

    [TestMethod]
    public void ACreateThatFailsKeepsItsRawCode()
    {
        using var temp = new TempFolder();
        var acl = new FakeAcl { FailCreate = true };

        StagingResult result = new StagingFolder(acl, temp.File("Earshot")).Create();

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Detail, "0x80070005");
    }

    // The one real execution of the folder-security helper: a folder made through DirectorySecurity with a DACL naming only the
    // current user, read back as SDDL, and removed. (The administrators-only descriptor needs an elevated token to apply, so
    // the real helper is run with a descriptor the test's own token can make and still delete.)
    [TestMethod]
    public void TheRealHelperAppliesAProtectedDaclAndReadsItBack()
    {
        using var temp = new TempFolder();
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string path = temp.File("real-acl");
        string sddl = "D:P(A;OICI;FA;;;" + sid + ")";
        var acl = new NtfsFolderAcl();

        AclStep made = acl.CreateWithSddl(path, sddl, failIfExists: true);
        AclStep again = acl.CreateWithSddl(path, sddl, failIfExists: true);
        AclStep read = acl.ReadSddl(path, out string? back);

        Assert.IsTrue(made.Ok, made.Detail);
        Assert.IsFalse(again.Ok, "A folder that is already there is not trusted.");
        Assert.IsTrue(read.Ok, read.Detail);
        var descriptor = new RawSecurityDescriptor(back!);
        Assert.AreEqual(ControlFlags.DiscretionaryAclProtected, descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected, "The DACL is protected.");
        GenericAce only = descriptor.DiscretionaryAcl!.Cast<GenericAce>().Single();
        Assert.AreEqual(sid, ((QualifiedAce)only).SecurityIdentifier.Value);
        Assert.IsTrue(acl.Exists(path));
        Assert.IsFalse(acl.ReadSddl(temp.File("missing"), out _).Ok);
    }

    // ---- the launcher ----

    [TestMethod]
    public void TheRealLauncherRunsAProgramAndReturnsItsExitCode()
    {
        var launcher = new WindowsProcessLauncher();
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        LaunchResult zero = launcher.RunAndWait(cmd, ["/c", "exit", "0"], Environment.SystemDirectory, TimeSpan.FromSeconds(30));
        LaunchResult three = launcher.RunAndWait(cmd, ["/c", "exit", "3"], Environment.SystemDirectory, TimeSpan.FromSeconds(30));
        LaunchResult missing = launcher.RunAndWait(Path.Combine(Environment.SystemDirectory, "no-such-program.exe"), [], Environment.SystemDirectory, TimeSpan.FromSeconds(5));

        Assert.IsTrue(zero.Started);
        Assert.AreEqual(0, zero.ExitCode);
        Assert.AreEqual(3, three.ExitCode);
        Assert.IsFalse(missing.Started);
        Assert.AreEqual(-1, missing.ExitCode);
        Assert.AreNotEqual("", missing.Detail, "The reason is kept.");
    }

    [TestMethod]
    public void TheRuntimeVariablesAreNotPassedToAnElevatedProgram()
    {
        using var guard = new EnvironmentVariableScope("DOTNET_EarshotSetupTest", "1");
        using var other = new EnvironmentVariableScope("EarshotSetupKeep", "1");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        LaunchResult result = new WindowsProcessLauncher().RunAndWait(
            cmd, ["/c", "if defined DOTNET_EarshotSetupTest (exit 7) else if defined EarshotSetupKeep (exit 0) else (exit 8)"],
            Environment.SystemDirectory, TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, result.ExitCode, "The DOTNET_ variable is dropped and an ordinary one is kept.");
        Assert.IsTrue(WindowsProcessLauncher.IsRuntimeVariable("coreclr_enable_profiling"));
        Assert.IsTrue(WindowsProcessLauncher.IsRuntimeVariable("COMPlus_ReadyToRun"));
        Assert.IsTrue(WindowsProcessLauncher.IsRuntimeVariable("COR_PROFILER"));
        Assert.IsFalse(WindowsProcessLauncher.IsRuntimeVariable("EARSHOT_SAFE_MODE"), "Earshot's own safety variables still reach it.");
        Assert.IsFalse(WindowsProcessLauncher.IsRuntimeVariable("PATH"));
    }

    // ---- values from the extracted program ----

    [TestMethod]
    public void TheValuesFileIsReadWithItsCandidatesAndAMissingCandidateListStillWorks()
    {
        SetupValuesReading? one = SetupValuesReading.Parse(
            "{\"userSid\":\"S-1-5-21-1-2-3-1001\",\"ready\":true,\"address\":\"0A1B2C3D4E5F\",\"containerId\":\"11111111-2222-3333-4444-555555555555\",\"install\":{\"state\":\"usable\",\"version\":\"1.4.0\"}}");
        Assert.IsNotNull(one);
        Assert.HasCount(1, one.Choices());
        Assert.AreEqual("usable", one.InstallState);
        Assert.AreEqual("1.4.0", one.InstalledVersion);

        Assert.IsNull(SetupValuesReading.Parse("not json"));
        Assert.IsNull(SetupValuesReading.Parse("[]"));
    }

    [TestMethod]
    public void AValuesFileWithAnOddAddressIsNeverReady()
    {
        SetupValuesReading? reading = SetupValuesReading.Parse(
            "{\"userSid\":\"S-1-5-21-1-2-3-1001\",\"ready\":true,\"address\":\"not-an-address\",\"containerId\":\"11111111-2222-3333-4444-555555555555\"}");

        Assert.IsNotNull(reading);
        Assert.IsFalse(reading.Ready);
        Assert.IsEmpty(reading.Choices());
        Assert.IsFalse(SetupValuesReading.IsUserSid("S-1-5-21-1 & calc"));
        Assert.IsTrue(SetupValuesReading.IsUserSid("S-1-5-21-111-222-333-1001"));
    }

    // ---- the native structures and the built program ----

    [TestMethod]
    public void TheTaskDialogStructuresAreTheSizesCommctrlDeclares()
    {
        // TASKDIALOGCONFIG is declared between pshpack1.h and poppack.h: 160 bytes on x64. TASKDIALOG_BUTTON is an int and a
        // pointer, packed: 12.
        Assert.AreEqual(160, Marshal.SizeOf<NativeTaskDialog.Config>());
        Assert.AreEqual(12, Marshal.SizeOf<NativeTaskDialog.Button>());
    }

    [TestMethod]
    public void SetupAsksForAdministratorRightsAndLimitsWhereItLoadsCodeFrom()
    {
        string setup = Path.Combine(RepositoryRoot(), "src", "Earshot.Setup");
        string manifest = File.ReadAllText(Path.Combine(setup, "app.manifest"));
        string project = File.ReadAllText(Path.Combine(setup, "Earshot.Setup.csproj"));
        string program = File.ReadAllText(Path.Combine(setup, "SetupProgram.cs"));

        StringAssert.Contains(manifest, "level=\"requireAdministrator\"");
        StringAssert.Contains(project, "/DEPENDENTLOADFLAG:0x800");
        uint flag = SetupProgram.LoadLibrarySearchSystem32;
        Assert.AreEqual(0x800u, flag);
        int main = program.IndexOf("private static int Main()", StringComparison.Ordinal);
        int restrict = program.IndexOf("SetDefaultDllDirectories(LoadLibrarySearchSystem32)", main, StringComparison.Ordinal);
        int firstOther = program.IndexOf("new SetupFlow", main, StringComparison.Ordinal);
        Assert.IsTrue(main > 0 && restrict > main && restrict < firstOther, "The DLL search is limited before anything else in Main.");
    }

    [TestMethod]
    public void SetupDoesNotReferenceWinFormsOrTheApplication()
    {
        string project = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Earshot.Setup", "Earshot.Setup.csproj"));

        Assert.IsFalse(project.Contains("UseWindowsForms", StringComparison.Ordinal));
        Assert.IsFalse(project.Contains("ProjectReference", StringComparison.Ordinal));
        Assert.IsFalse(typeof(SetupFlow).Assembly.GetReferencedAssemblies().Any(a => a.Name is "System.Windows.Forms" or "Earshot"));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Earshot.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
