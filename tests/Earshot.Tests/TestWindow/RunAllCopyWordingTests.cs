using System.Reflection;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// No em-dash (already scanned in src), none of "unattended",
// "automatic", "batch" in Run all copy, no "GUI" in any string the owner sees. Reads every
// owner-facing string Copy.cs defines by reflection, so a new constant or method is covered
// automatically rather than needing this test edited every time one is added.
[TestClass]
public sealed class RunAllCopyWordingTests
{
    private static readonly string[] BannedInRunAllCopy = { "unattended", "automatic", "batch" };

    [TestMethod]
    public void NoStaticCopyStringNamesRunningUnattendedAutomaticallyOrInABatch()
    {
        foreach ((string name, string value) in AllStaticStringConstants())
        {
            foreach (string banned in BannedInRunAllCopy)
            {
                Assert.IsFalse(
                    value.Contains(banned, StringComparison.OrdinalIgnoreCase),
                    "Copy." + name + " contains '" + banned + "': \"" + value + "\"");
            }
        }
    }

    [TestMethod]
    public void NoStaticCopyStringEverSaysGui()
    {
        foreach ((string name, string value) in AllStaticStringConstants())
        {
            Assert.IsFalse(value.Contains("GUI", StringComparison.OrdinalIgnoreCase), "Copy." + name + " says \"GUI\": \"" + value + "\"");
        }
    }

    [TestMethod]
    public void TheRunAllButtonAndExplanationExist()
    {
        StringAssert.Contains(Copy.RunAllButtonLabel, "Run all");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Copy.RunAllExplanation));
    }

    [TestMethod]
    public void TheBuiltStoppedForPowerCycleMessageNamesTheTestAndStaysFreeOfBannedWords()
    {
        string message = Copy.RunAllStoppedForPowerCycle("08");
        StringAssert.Contains(message, "08");
        foreach (string banned in BannedInRunAllCopy)
        {
            Assert.IsFalse(message.Contains(banned, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void TheBuiltStoppedForFailureMessageNamesTheTest()
    {
        string message = Copy.RunAllStoppedForFailure("11");
        StringAssert.Contains(message, "11");
    }

    // Every field on Copy that is a plain string, whether a compile-time const or a static
    // readonly, by name and value.
    private static IEnumerable<(string Name, string Value)> AllStaticStringConstants()
    {
        foreach (FieldInfo field in typeof(Copy).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string))
            {
                continue;
            }

            string? value = (string?)field.GetValue(null);
            if (value is not null)
            {
                yield return (field.Name, value);
            }
        }
    }
}
