using System.Reflection;
using System.Reflection.Emit;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

[TestClass]
public sealed class ReleaseVersionTests
{
    private static ReleaseVersion V(string text) =>
        ReleaseVersion.TryParse(text, out ReleaseVersion v) ? v : throw new AssertFailedException("Not a version: " + text);

    // Newer, older or equal, read number by number. The last two rows are the ones a text comparison gets wrong.
    [TestMethod]
    [DataRow("1.2.0", "1.1.0", 1)]
    [DataRow("1.1.0", "1.2.0", -1)]
    [DataRow("1.1.0", "1.1.0", 0)]
    [DataRow("v1.1.0", "1.1.0", 0)]
    [DataRow("1.1.1", "1.1.0", 1)]
    [DataRow("2.0.0", "1.99.99", 1)]
    [DataRow("1.10.0", "1.9.0", 1)]
    [DataRow("1.9.0", "1.10.0", -1)]
    [DataRow("1.2.10", "1.2.9", 1)]
    [DataRow("10.0.0", "9.0.0", 1)]
    [DataRow("1.2.0.1", "1.2.0", 1)]
    [DataRow("1.2.0.0", "1.2.0", 0)]
    [DataRow("1.02.0", "1.2.0", 0)]
    public void VersionsCompareNumberByNumber(string left, string right, int expected)
    {
        Assert.AreEqual(expected, Math.Sign(V(left).CompareTo(V(right))));
        Assert.AreEqual(expected > 0, V(left) > V(right));
        Assert.AreEqual(expected < 0, V(left) < V(right));
        Assert.AreEqual(expected >= 0, V(left) >= V(right));
        Assert.AreEqual(expected <= 0, V(left) <= V(right));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("v")]
    [DataRow("1")]
    [DataRow("1.2")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1.2.x")]
    [DataRow("1.2.0-beta")]
    [DataRow("1.2.0+build5")]
    [DataRow("vv1.2.0")]
    [DataRow(" 1.2.0")]
    [DataRow("1.2.0 ")]
    [DataRow("1..0")]
    [DataRow("-1.2.0")]
    [DataRow("+1.2.0")]
    [DataRow("1.2.0.")]
    [DataRow("1.2.99999999999")]
    [DataRow("latest")]
    public void ATagThatIsNotThreeOrFourNumbersIsNotAVersion(string text)
    {
        Assert.IsFalse(ReleaseVersion.TryParse(text, out _));
    }

    [TestMethod]
    public void AVersionPrintsWithoutTheTagPrefixAndWithoutAZeroFourthNumber()
    {
        Assert.AreEqual("1.2.0", V("v1.2.0").ToString());
        Assert.AreEqual("1.2.0", V("1.2.0.0").ToString());
        Assert.AreEqual("1.2.0.3", V("1.2.0.3").ToString());
    }

    [TestMethod]
    public void TheRunningProgramReportsTheVersionTheProjectFileNames()
    {
        ReleaseVersion? running = ReleaseVersion.Running(typeof(Earshot.Update.ReleaseVersion).Assembly);

        Assert.IsNotNull(running, "Earshot.exe reports no readable informational version.");
        string csproj = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Earshot", "Earshot.csproj"));
        int start = csproj.IndexOf("<Version>", StringComparison.Ordinal) + "<Version>".Length;
        string written = csproj[start..csproj.IndexOf("</Version>", StringComparison.Ordinal)];
        Assert.AreEqual(written, running.Value.ToString(), "The update check compares against the version written in the project file.");
    }

    [TestMethod]
    public void BuildMetadataAfterAPlusIsIgnoredWhenReadingTheRunningVersion()
    {
        static ReleaseVersion? Read(string informational)
        {
            ConstructorInfo attribute = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("VersionProbe" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run,
                [new CustomAttributeBuilder(attribute, [informational])]);
            return ReleaseVersion.Running(assembly);
        }

        Assert.AreEqual(V("1.2.3"), Read("1.2.3+abcdef0123"));
        Assert.AreEqual(V("1.2.3"), Read("1.2.3"));
        Assert.IsNull(Read("1.2.3-beta+abc"), "A pre-release suffix is not a release version.");
        Assert.IsNull(Read("not a version"));
        Assert.IsFalse(ReleaseVersion.TryParse("1.2.0+abc123", out _), "TryParse itself never accepts metadata.");
    }
}
