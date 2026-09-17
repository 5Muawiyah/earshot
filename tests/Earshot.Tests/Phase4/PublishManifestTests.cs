using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The publish step itself: a real self-contained publish of the application into a temporary folder (with its
// build output under a temporary artifacts path too, so the working tree's obj folder is left alone), then the
// manifest it wrote is read the way install reads it. A self-contained publish is used because it puts files in
// culture folders, which is where a manifest path can go wrong. Nothing is installed.
[TestClass]
[TestCategory("Publish")]
public sealed class PublishManifestTests
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(8);

    [TestMethod]
    public void APublishListsEveryPublishedFileWithItsHash()
    {
        using var temp = new TempFolder();
        string output = Path.Combine(temp.Path, "out");
        string artifacts = Path.Combine(temp.Path, "artifacts");
        string project = Path.Combine(RepositoryRoot(), "src", "Earshot", "Earshot.csproj");

        (int exit, string log) = Publish(project, output, artifacts, selfContained: true);
        if (exit != 0 && log.Contains("error NU1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("The runtime packages for a self-contained publish could not be restored here:" + Environment.NewLine + Tail(log));
        }

        Assert.AreEqual(0, exit, "dotnet publish failed:" + Environment.NewLine + Tail(log));

        FileManifest? manifest = FileManifest.Read(output, out StepOutcome step);
        Assert.IsNotNull(manifest, step.Detail);

        var published = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(output, f))
            .Where(f => !string.Equals(f, FileManifest.FileName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = manifest.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.AreEquivalent(published.Order(StringComparer.OrdinalIgnoreCase).ToArray(), listed.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            "The manifest lists exactly the published files, the manifest itself excepted.");
        Assert.Contains("Earshot.exe", listed);
        Assert.IsTrue(listed.Any(f => f.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "Files in culture folders keep their folder.");

        foreach (ManifestFile file in manifest.Files)
        {
            byte[] content = File.ReadAllBytes(Path.Combine(output, file.RelativePath));
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)), file.Sha256.ToUpperInvariant(), file.RelativePath);
        }
    }

    // A file an earlier publish left in the folder is never listed: the publish stops, with no manifest, and
    // says to publish into an empty folder. Framework-dependent, which is enough here and quicker.
    [TestMethod]
    public void APublishIntoAFolderHoldingOtherFilesWritesNoManifest()
    {
        using var temp = new TempFolder();
        string output = Path.Combine(temp.Path, "out dir");
        string artifacts = Path.Combine(temp.Path, "artifacts");
        string project = Path.Combine(RepositoryRoot(), "src", "Earshot", "Earshot.csproj");
        Directory.CreateDirectory(Path.Combine(output, "de"));
        File.WriteAllText(Path.Combine(output, "leftover-from-earlier.dll"), "not published now");
        File.WriteAllText(Path.Combine(output, "de", "leftover.resources.dll"), "not published now");
        File.WriteAllText(Path.Combine(output, FileManifest.FileName), "{ \"stale\": true }");

        (int exit, string log) = Publish(project, output, artifacts, selfContained: false);
        if (exit != 0 && log.Contains("error NU1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("The packages for the publish could not be restored here:" + Environment.NewLine + Tail(log));
        }

        Assert.AreNotEqual(0, exit, "The publish must stop:" + Environment.NewLine + Tail(log));
        StringAssert.Contains(log, "Publish into an empty folder.");
        StringAssert.Contains(log, "leftover-from-earlier.dll");
        StringAssert.Contains(log, "leftover.resources.dll");
        Assert.IsFalse(File.Exists(Path.Combine(output, FileManifest.FileName)), "No manifest, not even the stale one, is left.");
    }

    private static (int Exit, string Log) Publish(string project, string output, string artifacts, bool selfContained)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] mode = selfContained
            ? ["-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false"]
            : [];
        foreach (string argument in new[] { "publish", project, "-c", "Release" }
                     .Concat(mode)
                     .Concat(["-o", output, "--artifacts-path", artifacts, "-nologo", "-v:minimal"]))
        {
            info.ArgumentList.Add(argument);
        }

        // The test host's own MSBuild settings are not the publish's.
        foreach (string name in info.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        var log = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(PublishTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("dotnet publish did not finish within " + PublishTimeout + ":" + Environment.NewLine + Tail(log.ToString()));
        }

        process.WaitForExit();
        lock (log)
        {
            return (process.ExitCode, log.ToString());
        }
    }

    private static void Append(StringBuilder log, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (log)
        {
            log.AppendLine(line);
        }
    }

    // The dotnet that is running the tests, when the host says which; otherwise the one on the path.
    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static string Tail(string log) =>
        string.Join(Environment.NewLine, log.Split('\n').Select(l => l.TrimEnd('\r')).TakeLast(30));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
