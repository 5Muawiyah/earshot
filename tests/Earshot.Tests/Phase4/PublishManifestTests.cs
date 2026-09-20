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

        // The release carries binaries that are not Earshot's and not under Earshot's licence, so the notices that
        // name them travel with it: published, and listed in the manifest, so install copies and checks them like
        // every other file.
        Assert.Contains(NoticesFileName, listed);
        string notices = File.ReadAllText(Path.Combine(output, NoticesFileName));
        Assert.AreEqual(File.ReadAllText(Path.Combine(RepositoryRoot(), NoticesFileName)), notices, "What ships is the file in the repository.");

        // Every package the publish says it carries is named in the notices, so a dependency added later without a
        // notice fails here. The list is the publish's own (Earshot.deps.json), not one typed into this test.
        string depsFile = Path.Combine(output, "Earshot.deps.json");
        string[] carried = CarriedPackages(depsFile, "Earshot");
        Assert.IsTrue(carried.Length >= 4, "Only " + carried.Length + " packages were read from Earshot.deps.json: " + string.Join(", ", carried));
        foreach (string package in carried)
        {
            Assert.IsTrue(NamesPackage(notices, package), "The release carries " + package + " and " + NoticesFileName + " has no \"Package:\" line for it.");
        }

        foreach (string binary in ThirdPartyBinariesNamedOneByOne)
        {
            Assert.IsTrue(File.Exists(Path.Combine(output, binary)), binary + " is named in the notices and is not in the release.");
        }

        // The MIT licence asks that its copyright notice and its permission notice travel with every copy, so the
        // release carries the licence text of each component it ships under that licence, not only a link to it.
        // Each file is read from the publisher's own package at publish time, so it is present and listed here or
        // the publish stopped before it reached this test.
        foreach (string licence in LicenceTextsTheReleaseCarries)
        {
            string full = Path.Combine(output, licence);
            Assert.IsTrue(File.Exists(full), licence + " is not in the release, so the licence text does not travel with it.");
            Assert.Contains(licence, listed, licence + " ships and " + FileManifest.FileName + " does not list it, so install would not copy it.");
            Assert.IsTrue(new FileInfo(full).Length > 0, licence + " is empty.");
        }

        // Not just a file of the right name: the .NET runtime's own LICENSE.TXT is the MIT text, and the permission
        // notice is the sentence MIT asks to be carried. The notices file is the one place that must not hold it.
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(output, @"licences\Microsoft.NETCore.App.Runtime.win-x64\LICENSE.TXT")),
            "Permission is hereby granted",
            "The published .NET runtime licence carries no permission notice.");
        Assert.AreEqual(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENSE")),
            File.ReadAllText(Path.Combine(output, @"licences\Earshot\LICENSE")),
            "Earshot's own licence ships as the repository holds it: a release archive is not the source repository.");

        // Nothing ships that no notice answers for. The loop above can only ask about packages Earshot.deps.json
        // names; this asks about the published files themselves.
        string[] unaccounted = BinariesTheNoticesDoNotAccountFor(output, depsFile, EarshotsOwnBinaries, notices);
        Assert.AreEqual(0, unaccounted.Length,
            "The release ships binaries that no package in Earshot.deps.json accounts for and that " + NoticesFileName +
            " does not name: " + string.Join(", ", unaccounted));
    }

    // A third-party binary copied in by a plain <None ... CopyToPublishDirectory="PreserveNewest" /> item ships and
    // is listed in Earshot.files.json, but a deps file only knows about package and project references, so it names
    // no such file. The package-by-package check therefore cannot see it and never asks for a notice. This is that
    // gap, shown on a publish of its own: a throwaway project with one loose binary in it, so nothing has to be
    // pretended about Earshot's own release.
    [TestMethod]
    public void ALooseBinaryNoPackageAccountsForIsCaughtThoughTheDepsFileNeverNamesIt()
    {
        using var temp = new TempFolder();
        string source = Path.Combine(temp.Path, "fixture");
        string output = Path.Combine(temp.Path, "out");
        string artifacts = Path.Combine(temp.Path, "artifacts");
        Directory.CreateDirectory(source);

        // Empty files of both names stop MSBuild walking above the temporary folder for settings that are not this
        // fixture's. The binary is a dummy: the check reads file names and a deps file, never the bytes.
        File.WriteAllText(Path.Combine(source, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(source, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(source, "Program.cs"), "internal static class Program { private static void Main() { } }");
        File.WriteAllBytes(Path.Combine(source, OrphanBinary), [0x4D, 0x5A, 0x00, 0x00]);
        string project = Path.Combine(source, "Fixture.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Fixture</AssemblyName>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <None Include="Contoso.Native.dll" CopyToPublishDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);

        (int exit, string log) = Publish(project, output, artifacts, selfContained: false);
        if (exit != 0 && log.Contains("error NU1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("The fixture could not be restored here:" + Environment.NewLine + Tail(log));
        }

        Assert.AreEqual(0, exit, "The fixture publish failed:" + Environment.NewLine + Tail(log));
        Assert.IsTrue(File.Exists(Path.Combine(output, OrphanBinary)), OrphanBinary + " did not reach the publish folder, so there is no gap to show.");

        // The gap itself. The deps file of a publish carrying that binary names no third-party package at all, so
        // the package-by-package check has nothing to ask about and passes on a release that ships a stranger.
        string depsFile = Path.Combine(output, "Fixture.deps.json");
        string[] carried = CarriedPackages(depsFile, "Fixture");
        Assert.AreEqual(0, carried.Length,
            "The fixture's deps file names packages, so it does not show a binary that no deps entry accounts for: " + string.Join(", ", carried));
        Assert.IsFalse(File.ReadAllText(depsFile).Contains("Contoso", StringComparison.OrdinalIgnoreCase),
            "The deps file names the loose binary after all, so the package-by-package check would have seen it.");

        // The check that does see it. With notices that say nothing, the binary is reported.
        string[] unnamed = BinariesTheNoticesDoNotAccountFor(output, depsFile, FixtureOwnBinaries, string.Empty);
        Assert.Contains(OrphanBinary, unnamed,
            "A loose third-party binary ships, no deps entry accounts for it and no notice names it, and the check let it through. It reported: " +
            (unnamed.Length == 0 ? "nothing" : string.Join(", ", unnamed)));

        // And with notices that name it, it is not reported, so the check asks for a notice rather than forbidding
        // the file.
        string[] named = BinariesTheNoticesDoNotAccountFor(output, depsFile, FixtureOwnBinaries, "Contoso.Native.dll, published by Contoso, under its own terms.");
        Assert.IsFalse(named.Contains(OrphanBinary, StringComparer.OrdinalIgnoreCase),
            "A binary the notices name is still reported: " + string.Join(", ", named));
    }

    private const string OrphanBinary = "Contoso.Native.dll";
    private static readonly string[] FixtureOwnBinaries = ["Fixture.exe", "Fixture.dll"];

    // The apphost. Earshot.dll is named by Earshot's own entry in the deps file; the exe the SDK builds around it
    // is in no deps file, and it is not third party.
    private static readonly string[] EarshotsOwnBinaries = ["Earshot.exe"];

    // Where each carried licence text lands in the release, and what THIRD-PARTY-NOTICES.txt says about it. The
    // paths are the ones the publish writes, so a folder renamed on one side and not the other fails.
    private static readonly string[] LicenceTextsTheReleaseCarries =
    [
        @"licences\Microsoft.NETCore.App.Runtime.win-x64\LICENSE.TXT",
        @"licences\Microsoft.NETCore.App.Runtime.win-x64\THIRD-PARTY-NOTICES.TXT",
        @"licences\Microsoft.WindowsDesktop.App.Runtime.win-x64\LICENSE",
        @"licences\System.Speech\THIRD-PARTY-NOTICES.TXT",
        @"licences\Earshot\LICENSE",
    ];

    // Every published .dll and .exe that is neither the application's own nor accounted for by an entry in the deps
    // file has to be named in the notices by its file name. A deps file lists the assemblies each package and each
    // runtime pack contributed, so a file whose name is in that list is already covered by the package-by-package
    // check; anything else arrived some other way and would otherwise be asked about by nothing.
    private static string[] BinariesTheNoticesDoNotAccountFor(string publishDir, string depsFile, string[] ownBinaries, string notices)
    {
        var accounted = new HashSet<string>(ownBinaries, StringComparer.OrdinalIgnoreCase);
        using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile)))
        {
            foreach (System.Text.Json.JsonProperty target in document.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (System.Text.Json.JsonProperty library in target.Value.EnumerateObject())
                {
                    foreach (string section in DepsAssetSections)
                    {
                        if (!library.Value.TryGetProperty(section, out System.Text.Json.JsonElement assets))
                        {
                            continue;
                        }

                        foreach (System.Text.Json.JsonProperty asset in assets.EnumerateObject())
                        {
                            accounted.Add(Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar)));
                        }
                    }
                }
            }
        }

        return Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(publishDir, f))
            .Where(f => BinaryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !accounted.Contains(Path.GetFileName(f)))
            .Where(f => !IsSatelliteOf(Path.GetFileName(f), accounted))
            .Where(f => !notices.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly string[] DepsAssetSections = ["runtime", "native", "resources"];
    private static readonly string[] BinaryExtensions = [".dll", ".exe"];

    // A self-contained publish puts satellite assemblies in culture folders and names none of them in the deps
    // file. One belongs to the package its parent assembly came from, so it is accounted for when the parent is.
    private static bool IsSatelliteOf(string fileName, HashSet<string> accounted)
    {
        const string suffix = ".resources.dll";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && accounted.Contains(string.Concat(fileName.AsSpan(0, fileName.Length - suffix.Length), ".dll"));
    }

    // The notices as they stand in the repository: each third-party binary by name, who publishes it, the name of its
    // licence and where its terms are, and no licence text copied out. Quick, and needs no publish.
    [TestMethod]
    public void TheNoticesNameEachThirdPartyBinaryItsPublisherItsLicenceAndItsTerms()
    {
        string notices = File.ReadAllText(Path.Combine(RepositoryRoot(), NoticesFileName));

        foreach (string binary in ThirdPartyBinariesNamedOneByOne)
        {
            StringAssert.Contains(notices, binary);
        }

        foreach (string expected in NoticesMustHold)
        {
            StringAssert.Contains(notices, expected);
        }

        foreach (string package in PackagesTheReleaseCarries)
        {
            Assert.IsTrue(NamesPackage(notices, package), NoticesFileName + " has no \"Package:\" line for " + package + ".");
        }

        // The licence texts travel with the release, and the notices say where each one lands. A folder renamed on
        // one side only fails here as well as in the publish test.
        foreach (string licence in LicenceTextsTheReleaseCarries)
        {
            StringAssert.Contains(notices, licence);
        }

        // Named and linked, never copied out.
        foreach (string licenceText in LicenceTextFragments)
        {
            Assert.IsFalse(notices.Contains(licenceText, StringComparison.OrdinalIgnoreCase), "The notices reproduce licence text: " + licenceText);
        }

        Assert.IsFalse(notices.Contains((char)0x2014), "An em-dash.");
    }

    private const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    // As the self-contained publish names them in Earshot.deps.json today. The publish test reads that file itself, so
    // a package added later is caught there; this list is for the quick test, which has no publish to read.
    private static readonly string[] PackagesTheReleaseCarries =
    [
        "Microsoft.Windows.SDK.NET.Ref",
        "System.Speech",
        "Microsoft.NETCore.App.Runtime.win-x64",
        "Microsoft.WindowsDesktop.App.Runtime.win-x64",
    ];

    // A line of its own, "Package:" and then exactly the name: "System.Speech.dll" in a heading does not name the
    // package System.Speech, and one name that begins another does not stand in for it.
    private static bool NamesPackage(string notices, string package) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            notices,
            "^\\s*Package:\\s+" + System.Text.RegularExpressions.Regex.Escape(package) + "\\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] NoticesMustHold =
    [
        "Microsoft Corporation",
        "Microsoft Software License Terms",
        "This is not the MIT licence.",
        "https://learn.microsoft.com/en-us/legal/windows-sdk/license",
        "https://learn.microsoft.com/en-us/legal/windows-sdk/redist",
        "https://licenses.nuget.org/MIT",
        "unmodified",
    ];

    private static readonly string[] LicenceTextFragments = ["Permission is hereby granted", "WITHOUT WARRANTY OF ANY KIND", "INSTALLATION AND USE RIGHTS"];

    // The ones that arrive as single files beside Earshot.exe. The two runtimes are hundreds of files and are named
    // as packages.
    private static readonly string[] ThirdPartyBinariesNamedOneByOne = ["Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll", "System.Speech.dll"];

    // The "libraries" of a deps file name every package the publish carries, as "name/version", runtime packs with a
    // "runtimepack." prefix. The project's own entry is the one that is not third party.
    private static string[] CarriedPackages(string depsFile, string ownLibrary)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile));
        return document.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0])
            .Select(name => name.StartsWith("runtimepack.", StringComparison.Ordinal) ? name["runtimepack.".Length..] : name)
            .Where(name => !string.Equals(name, ownLibrary, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
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
