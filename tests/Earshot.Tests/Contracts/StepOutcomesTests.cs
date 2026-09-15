using System.Text.RegularExpressions;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

[TestClass]
public sealed partial class StepOutcomesTests
{
    [TestMethod]
    public void AConfigRetIsDecodedAsACfgMgr32Code()
    {
        StepOutcome step = StepOutcomes.FromConfigRet("cm-locate:BTHENUM\\DEV_5A6B7C8D9EAF", 0x5);

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(5, step.Code);
        Assert.AreEqual("CR_INVALID_DEVNODE", step.CodeName);
    }

    [TestMethod]
    public void AWin32CodeIsDecodedAsAWin32Code()
    {
        StepOutcome step = StepOutcomes.FromWin32("bt-set-service:111E", 0x5);

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(5, step.Code);
        Assert.AreEqual("ERROR_ACCESS_DENIED", step.CodeName);
    }

    [TestMethod]
    public void OkFollowsTheFamilyUnlessTheCallerSaysOtherwise()
    {
        Assert.IsTrue(StepOutcomes.FromConfigRet("s", 0).Ok);
        Assert.IsTrue(StepOutcomes.FromWin32("s", 0).Ok);
        Assert.IsTrue(StepOutcomes.FromHResult("s", 1).Ok, "S_FALSE succeeds.");
        Assert.IsFalse(StepOutcomes.FromHResult("s", unchecked((int)0x80070490)).Ok);

        StepOutcome moreData = StepOutcomes.FromWin32("bt-installed-services", 234, ok: true);
        Assert.IsTrue(moreData.Ok);
        Assert.AreEqual("ERROR_MORE_DATA", moreData.CodeName);

        StepOutcome alreadyInState = StepOutcomes.FromWin32("bt-set-service:111E", 0x80070057, "already in state", ok: true);
        Assert.IsTrue(alreadyInState.Ok);
        Assert.AreEqual(unchecked((int)0x80070057), alreadyInState.Code);
        Assert.AreEqual("E_INVALIDARG", alreadyInState.CodeName);
        Assert.AreEqual("already in state", alreadyInState.Detail);
    }

    [TestMethod]
    public void AnHresultIsDecodedByName()
    {
        StepOutcome step = StepOutcomes.FromHResult("activate-topology", unchecked((int)0xE0000225));

        Assert.IsFalse(step.Ok);
        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", step.CodeName);
    }

    // Production code never decodes a CONFIGRET or a Win32 code with Name, which is what a cast from
    // uint to int in front of Name gives away. StepOutcomes, ConfigRet and Win32 are the way.
    [TestMethod]
    public void ProductionCodeNeverSendsARawUnsignedCodeThroughName()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) || file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (NameWithCast().IsMatch(lines[i]))
                {
                    offenders.Add(Path.GetRelativePath(src, file) + ":" + (i + 1));
                }
            }
        }

        Assert.IsEmpty(offenders, string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex(@"NativeCodes\.Name\(\s*(unchecked\s*\(\s*)?\(\s*int\s*\)")]
    private static partial Regex NameWithCast();

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
