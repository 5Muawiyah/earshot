using System.Text;
using Earshot.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Setup;

// Setup from the first check to the last message, with a scripted screen, a fake launcher and a fake folder security. No test
// starts the built Setup.exe, raises the administrator prompt or runs an extracted program.
[TestClass]
public sealed class SetupFlowTests
{
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string AddressA = "0A1B2C3D4E5F";
    private const string AddressB = "0A1B2C3D4E60";
    private const string ContainerA = "11111111-2222-3333-4444-555555555555";
    private const string ContainerB = "66666666-7777-8888-9999-000000000000";

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World(string version = "1.5.0", byte[]? zip = null, string? recordedHash = null, bool embedded = true, bool elevated = true)
        {
            Machine = _temp.File(Path.Combine("ProgramData", "Earshot"));
            InstalledExe = _temp.File(Path.Combine("ProgramFiles", "Earshot", "Earshot.exe"));
            Payload = new FakePayload(zip ?? ReleaseZip.Build(version), version, recordedHash, embedded);
            Host = new FakeHost(Machine, InstalledExe, elevated);
            Directory.CreateDirectory(Machine);
        }

        public string Machine { get; }

        public string InstalledExe { get; }

        public FakePayload Payload { get; }

        public FakeHost Host { get; }

        public FakeAcl Acl { get; } = new();

        public FakeLauncher Launcher { get; } = new();

        public ScriptedUi Ui { get; } = new();

        public string StagingRoot => Path.Combine(Machine, StagingFolder.FolderName);

        public SetupOutcome Run() => new SetupFlow(Payload, Acl, Launcher, Ui, Host).Run();

        public void Dispose() => _temp.Dispose();
    }

    private static string Values(string state = "nothing", string installed = "", bool ready = true, string address = AddressA, string container = ContainerA, string? candidates = null) =>
        "{\"schema\":1,\"userSid\":\"" + UserSid + "\",\"ready\":" + (ready ? "true" : "false") + ",\"reason\":\"\",\"address\":\"" + (ready ? address : "") +
        "\",\"containerId\":\"" + (ready ? container : "") + "\",\"source\":\"paired\",\"install\":{\"state\":\"" + state + "\",\"problem\":\"\",\"version\":\"" + installed + "\"}" +
        (candidates is null ? "" : ",\"candidates\":" + candidates) + "}";

    [TestMethod]
    public void ATamperedPayloadIsRefusedBeforeAnyFolderIsMadeOrAnyProgramRun()
    {
        byte[] zip = ReleaseZip.Build("1.5.0");
        using var w = new World(zip: zip, recordedHash: new string('0', 64));

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.PayloadDamaged, outcome.ExitCode);
        Assert.AreEqual("The installer is damaged", outcome.Instruction);
        Assert.AreEqual("Download it again.", outcome.Detail);
        Assert.IsEmpty(w.Acl.Created, "No folder is made for a payload that fails its check.");
        Assert.IsEmpty(w.Launcher.Runs);
        Assert.IsFalse(Directory.Exists(w.StagingRoot));
        Assert.AreEqual(MessageKind.Error, w.Ui.Messages.Single().Kind);
    }

    [TestMethod]
    public void OneFlippedBitInThePayloadIsEnoughToRefuseIt()
    {
        byte[] zip = ReleaseZip.Build("1.5.0");
        string recorded = ReleaseZip.Sha256(zip);
        zip[zip.Length / 2] ^= 1;
        using var w = new World(zip: zip, recordedHash: recorded);

        Assert.AreEqual(SetupExit.PayloadDamaged, w.Run().ExitCode);
        Assert.IsEmpty(w.Launcher.Runs);
    }

    [TestMethod]
    public void ABuildWithNoPayloadRefusesAndSaysSo()
    {
        using var w = new World(embedded: false);

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.NoPayload, outcome.ExitCode);
        Assert.IsEmpty(w.Acl.Created);
        Assert.AreEqual(0, w.Payload.Opens, "A payload that is not there is not opened.");
    }

    [TestMethod]
    public void SetupThatIsNotElevatedDoesNothingAtAll()
    {
        using var w = new World(elevated: false);

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.NotElevated, outcome.ExitCode);
        Assert.AreEqual(0, w.Payload.Opens);
        Assert.IsEmpty(w.Acl.Created);
        Assert.IsEmpty(w.Launcher.Runs);
    }

    [TestMethod]
    public void AFreshInstallUnpacksInTheProtectedFolderThenRunsInstallZipWithTheCheckedValues()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values();

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        (string exe, string[] args) = w.Launcher.Runs[0];
        Assert.AreEqual("probe", args[0]);
        StringAssert.Contains(exe, StagingFolder.FolderName, "The extracted program runs from the protected staging folder.");
        Assert.IsTrue(exe.StartsWith(w.StagingRoot, StringComparison.OrdinalIgnoreCase));

        (string installExe, string[] install) = w.Launcher.Runs[1];
        Assert.AreEqual(exe, installExe);
        Assert.HasCount(6, install);
        Assert.AreEqual("install-zip", install[0]);
        Assert.AreEqual("update.zip", Path.GetFileName(install[1]));
        Assert.IsTrue(install[1].StartsWith(w.StagingRoot, StringComparison.OrdinalIgnoreCase), "The zip is the copy inside the protected folder.");
        Assert.AreEqual(w.Payload.Sha256.ToUpperInvariant(), install[2]);
        Assert.AreEqual(UserSid, install[3]);
        Assert.AreEqual(AddressA, install[4]);
        Assert.AreEqual(ContainerA, install[5]);
        CollectionAssert.AreEqual(new[] { w.InstalledExe }, w.Launcher.UserSessionStarts);
        Assert.IsFalse(Directory.Exists(Path.Combine(w.StagingRoot)), "The working folder and its empty parent are gone.");
        StringAssert.Contains(w.Ui.Footer, "does not show the file is genuine", "The wording says what the check does not do.");
    }

    [TestMethod]
    public void TheProtectedFolderIsMadeWithAnAdministratorsOnlyDescriptorAndReadBack()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values();

        w.Run();

        Assert.IsTrue(w.Acl.Created.Any(c => c.EndsWith("|" + StagingAcl.AdminOnlySddl, StringComparison.Ordinal) && c.Contains(StagingFolder.FolderName, StringComparison.Ordinal)));
        Assert.IsTrue(w.Acl.ReadPaths.Count >= 2, "Each level is read back before it is used.");
    }

    [TestMethod]
    public void AWorkingFolderThatReadsBackAsWritableByUsersStopsSetupBeforeAnythingIsUnpacked()
    {
        using var w = new World();
        w.Acl.ReadAs = path => path.Contains(StagingFolder.FolderName, StringComparison.OrdinalIgnoreCase)
            ? "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)"
            : StagingAcl.MachineFolderSddl;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.NoStaging, outcome.ExitCode);
        Assert.IsEmpty(w.Launcher.Runs);
        Assert.AreEqual(1, w.Payload.Opens, "Only the hash read opened the payload; nothing was unpacked.");
    }

    [TestMethod]
    public void SeveralAirPodsAskWhichAndInstallForTheOneChosen()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values(ready: false, candidates:
            "[{\"address\":\"" + AddressA + "\",\"containerId\":\"" + ContainerA + "\",\"name\":\"Red\",\"pid\":\"2027\"},{\"address\":\"" + AddressB + "\",\"containerId\":\"" + ContainerB + "\",\"name\":\"Blue\",\"pid\":\"2027\"}]");
        w.Ui.Pick = 1;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        Assert.AreEqual("Red|Blue", string.Join("|", w.Ui.Names));
        string[] install = w.Launcher.Runs.Single(r => r.Args[0] == "install-zip").Args;
        Assert.AreEqual(AddressB, install[4]);
        Assert.AreEqual(ContainerB, install[5]);
    }

    [TestMethod]
    public void CancellingTheAirPodsChoiceInstallsNothing()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values(ready: false, candidates:
            "[{\"address\":\"" + AddressA + "\",\"containerId\":\"" + ContainerA + "\",\"name\":\"Red\"},{\"address\":\"" + AddressB + "\",\"containerId\":\"" + ContainerB + "\",\"name\":\"Blue\"}]");
        w.Ui.Pick = null;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Cancelled, outcome.ExitCode);
        Assert.IsFalse(w.Launcher.Runs.Any(r => r.Args[0] == "install-zip"));
    }

    [TestMethod]
    public void NoPairedAirPodsIsSaidInPlainWordsAndInstallsNothing()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values(ready: false);

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.NoAirPods, outcome.ExitCode);
        StringAssert.Contains(outcome.Detail, "Pair your AirPods");
        Assert.IsFalse(w.Launcher.Runs.Any(r => r.Args[0] == "install-zip"));
    }

    [TestMethod]
    public void AnInstalledCopyOffersRepairFixAndUninstallAndNothingElse()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values("usable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.Cancel;

        w.Run();

        CollectionAssert.AreEqual(new[] { SetupAction.Repair, SetupAction.FixConnection, SetupAction.Uninstall }, w.Ui.Offers.Single().ToArray());
    }

    [TestMethod]
    public void RepairOfAUsableCopyRunsTheInstalledProgramsOwnRepairVerb()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values("usable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.Repair;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        (string exe, string[] args) = w.Launcher.Runs[1];
        Assert.AreEqual(w.InstalledExe, exe);
        CollectionAssert.AreEqual(new[] { "repair", UserSid, AddressA, ContainerA }, args);
    }

    [TestMethod]
    public void RepairOfAnUnusableCopyInstallsFromTheCheckedZipEvenWhenTheDownloadIsOlder()
    {
        using var w = new World(version: "1.4.0");
        w.Launcher.ValuesJson = Values("unusable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.Repair;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        Assert.AreEqual("install-zip", w.Launcher.Runs[1].Args[0], "Repair is the one action allowed to go to an older release.");
    }

    [TestMethod]
    public void InstallingOverANewerCopyIsRefusedAsADowngrade()
    {
        using var w = new World(version: "1.4.0");
        w.Launcher.ValuesJson = Values("unusable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.Install;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Downgrade, outcome.ExitCode);
        StringAssert.Contains(outcome.Detail, "1.5.0");
        StringAssert.Contains(outcome.Detail, "1.4.0");
        Assert.IsFalse(w.Launcher.Runs.Any(r => r.Args[0] == "install-zip"));
    }

    [TestMethod]
    public void UninstallRunsTheExtractedProgramsUninstallNotTheOneBeingDeleted()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values("usable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.Uninstall;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        (string exe, string[] args) = w.Launcher.Runs[1];
        Assert.AreEqual("uninstall", string.Join("|", args));
        Assert.IsTrue(exe.StartsWith(w.StagingRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FixConnectionRunsTheExtractedProgramsFixVerb()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values("usable", "1.5.0");
        w.Ui.Choose = _ => SetupAction.FixConnection;

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Done, outcome.ExitCode, outcome.Detail);
        Assert.AreEqual("fix", string.Join("|", w.Launcher.Runs[1].Args));
    }

    [TestMethod]
    public void ABadExitCodeIsShownWithItsNumberAndTheWorkingFolderIsStillRemoved()
    {
        using var w = new World();
        w.Launcher.ValuesJson = Values();
        w.Launcher.Handler = (_, args) => new Earshot.Setup.LaunchResult(true, args[0] == "install-zip" ? 3 : 0, "");

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Failed, outcome.ExitCode);
        StringAssert.Contains(outcome.Detail, "Code 3.");
        Assert.AreEqual(MessageKind.Error, w.Ui.Messages.Last().Kind);
        Assert.IsFalse(Directory.Exists(w.StagingRoot));
    }

    [TestMethod]
    public void BusyAndAlreadyInstalledHaveTheirOwnSentences()
    {
        Assert.AreEqual("Another set-up is running", SetupPlan.Describe(SetupAction.Install, 25).Instruction);
        Assert.AreEqual("Earshot is already installed", SetupPlan.Describe(SetupAction.Install, 26).Instruction);
        Assert.AreEqual("Setup is still finishing", SetupPlan.Describe(SetupAction.Install, 27).Instruction);
        Assert.IsFalse(SetupPlan.Describe(SetupAction.Install, 2).Ok);
    }

    [TestMethod]
    public void AReleaseWhoseFileListDisagreesWithItsFilesIsRefusedBeforeAnythingRuns()
    {
        byte[] zip = ReleaseZip.Build("1.5.0", hashOf: name => name == "Earshot.dll" ? new string('A', 64) : ReleaseZip.Sha256(Encoding.ASCII.GetBytes(name == "Earshot.exe" ? "exe 1.5.0" : "native 1.5.0")).ToUpperInvariant());
        using var w = new World(zip: zip);
        w.Launcher.ValuesJson = Values();

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Failed, outcome.ExitCode);
        StringAssert.Contains(outcome.Detail, "Earshot.dll");
        Assert.IsEmpty(w.Launcher.Runs, "An unpacked file that does not match its list is never run.");
    }

    [TestMethod]
    public void AValuesFileWithoutAUserSidStopsSetupBeforeAnythingIsInstalled()
    {
        using var w = new World();
        w.Launcher.ValuesJson = "{\"userSid\":\"\",\"ready\":true,\"address\":\"" + AddressA + "\",\"containerId\":\"" + ContainerA + "\"}";

        SetupOutcome outcome = w.Run();

        Assert.AreEqual(SetupExit.Failed, outcome.ExitCode);
        Assert.IsFalse(w.Launcher.Runs.Any(r => r.Args[0] == "install-zip"));
    }
}
