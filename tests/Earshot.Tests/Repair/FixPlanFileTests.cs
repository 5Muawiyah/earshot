using System.Diagnostics;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

[TestClass]
public sealed class FixPlanFileTests
{
    private static FixPlan SamplePlan()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        return FixPlanner.Build(rig.Nodes, rig.Records, rig.Leftovers).Plan;
    }

    [TestMethod]
    public void ThePlanRoundTripsAndTheHashIsOfTheBytesOnDisk()
    {
        using var temp = new TempFolder();
        var files = new FixPlanFiles(temp.Path);
        FixPlan plan = SamplePlan();

        FixPlanFileInfo info = files.Write(plan);

        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(info.Path))), info.Sha256);
        FixPlanRead read = FixPlanFileReader.ReadOnce(info.Path, info.Sha256);
        Assert.IsTrue(read.Ok, read.Problem);
        Assert.IsNotNull(read.Selection);
        Assert.HasCount(plan.Devices.SelectMany(d => d.Nodes).Count(), read.Selection.Nodes);
    }

    [TestMethod]
    public void AHashThatDoesNotMatchReadsNothing()
    {
        using var temp = new TempFolder();
        FixPlanFileInfo info = new FixPlanFiles(temp.Path).Write(SamplePlan());

        FixPlanRead read = FixPlanFileReader.ReadOnce(info.Path, new string('0', 64));

        Assert.IsFalse(read.Ok);
        Assert.IsNull(read.Selection);
        Assert.Contains("hash", read.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void AMissingFileReadsNothing()
    {
        using var temp = new TempFolder();

        FixPlanRead read = FixPlanFileReader.ReadOnce(Path.Combine(temp.Path, "x", "fix-plan.json"), new string('0', 64));

        Assert.IsFalse(read.Ok);
        Assert.IsNull(read.Selection);
    }

    [TestMethod]
    public void AFileWithAMemberThePlanNeverWritesIsRefusedEvenWithItsOwnHash()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "a");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FixPlanFiles.FileName);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Nodes\":[],\"Services\":[],\"Extra\":true}");
        File.WriteAllBytes(path, bytes);

        FixPlanRead read = FixPlanFileReader.ReadOnce(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

        Assert.IsFalse(read.Ok);
    }

    [TestMethod]
    public void AFileOverTheSizeCapIsRefused()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "a");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FixPlanFiles.FileName);
        byte[] bytes = new byte[FixPlanFileReader.MaxBytes + 1];
        Array.Fill(bytes, (byte)' ');
        File.WriteAllBytes(path, bytes);

        FixPlanRead read = FixPlanFileReader.ReadOnce(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

        Assert.IsFalse(read.Ok);
    }

    [TestMethod]
    public void AFileNotCalledByTheExpectedNameIsRefused()
    {
        using var temp = new TempFolder();
        string path = temp.File("other.json");
        byte[] bytes = "{\"SchemaVersion\":1,\"Nodes\":[],\"Services\":[]}"u8.ToArray();
        File.WriteAllBytes(path, bytes);

        FixPlanRead read = FixPlanFileReader.ReadOnce(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

        Assert.IsFalse(read.Ok);
    }

    [TestMethod]
    public void AFileReachedThroughAJunctionIsRefused()
    {
        using var temp = new TempFolder();
        FixPlanFileInfo info = new FixPlanFiles(Path.Combine(temp.Path, "real")).Write(SamplePlan());
        string linkFolder = Path.Combine(temp.Path, "link");
        var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + linkFolder + "\" \"" + Path.GetDirectoryName(info.Path) + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using (Process p = Process.Start(start)!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.AreEqual(0, p.ExitCode, "A junction could not be made for the test.");
        }

        try
        {
            FixPlanRead read = FixPlanFileReader.ReadOnce(Path.Combine(linkFolder, FixPlanFiles.FileName), info.Sha256);

            Assert.IsFalse(read.Ok);
            Assert.Contains("link", read.Problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Removes the junction itself, not what it points to.
            Directory.Delete(linkFolder, recursive: false);
        }
    }

    [TestMethod]
    public void AFileAnotherHandleHoldsOpenForWritingIsNotRead()
    {
        // The reader allows no writer while it reads, so a handle that is writing to the file makes the read fail rather than
        // return bytes that could change under the hash.
        using var temp = new TempFolder();
        FixPlanFileInfo info = new FixPlanFiles(temp.Path).Write(SamplePlan());
        using var writer = new FileStream(info.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        FixPlanRead read = FixPlanFileReader.ReadOnce(info.Path, info.Sha256);

        Assert.IsFalse(read.Ok);
    }
}
