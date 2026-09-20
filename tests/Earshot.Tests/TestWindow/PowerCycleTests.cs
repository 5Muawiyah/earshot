using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// test-gui.md section 9.3's three rules, each pinned by its own JSON shape. PowerCycle.Decide
// reads nothing itself: every case here hands it the exact raw shape Get-PowerCycleEvidence.ps1
// prints, so a change to that script's member names shows up here rather than only live.
[TestClass]
public sealed class PowerCycleTests
{
    [TestMethod]
    public void InvalidJsonIsUnknown()
    {
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide("not json"));
    }

    [TestMethod]
    public void AnErrorMemberIsUnknownEvenWhenOtherMembersLookValid()
    {
        string json = """{"error":"Get-WinEvent failed","kernelGeneral12":[],"kernelPower109":[]}""";
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void MissingKernelGeneral12MemberIsUnknown()
    {
        string json = """{"kernelPower109":[]}""";
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void NoStartAtAllIsNotYet()
    {
        string json = """{"kernelGeneral12":[],"kernelPower109":[{"utc":"2026-09-19T22:00:00Z","shutdownActionType":6}]}""";
        Assert.AreEqual(PowerCycleVerdict.NotYet, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void NoQualifying109BeforeTheNewestStartIsUnknown()
    {
        // A start exists, but nothing at all in kernelPower109 precedes it: rule 3's "none".
        string json = """{"kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],"kernelPower109":[{"utc":"2026-09-20T09:00:00Z","shutdownActionType":6}]}""";
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void ShutdownActionType6BeforeTheNewestStartIsPowerDown()
    {
        string json = """{"kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],"kernelPower109":[{"utc":"2026-09-20T07:55:00Z","shutdownActionType":6}]}""";
        Assert.AreEqual(PowerCycleVerdict.PowerDown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void ShutdownActionType4BeforeTheNewestStartIsPowerDown()
    {
        string json = """{"kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],"kernelPower109":[{"utc":"2026-09-20T07:55:00Z","shutdownActionType":4}]}""";
        Assert.AreEqual(PowerCycleVerdict.PowerDown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void ShutdownActionType5BeforeTheNewestStartIsRestart()
    {
        string json = """{"kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],"kernelPower109":[{"utc":"2026-09-20T07:55:00Z","shutdownActionType":5}]}""";
        Assert.AreEqual(PowerCycleVerdict.Restart, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void AnUnrecognisedShutdownActionTypeIsUnknownNeverAcceptedAsAShutDown()
    {
        string json = """{"kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],"kernelPower109":[{"utc":"2026-09-20T07:55:00Z","shutdownActionType":99}]}""";
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void The109UsedIsTheNewestOneStrictlyBeforeTheNewestStartNotAnOlderOne()
    {
        // Two 109 rows before the start: a restart, then (older) a power-down. The newest of the
        // two (the restart) decides, matching section 9.3's "a mistaken restart is put right by a
        // proper shut down": here it is the other way round on purpose, to prove "newest" wins
        // rather than "any".
        string json = """
            {
                "kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],
                "kernelPower109":[
                    {"utc":"2026-09-20T01:00:00Z","shutdownActionType":6},
                    {"utc":"2026-09-20T07:55:00Z","shutdownActionType":5}
                ]
            }
            """;
        Assert.AreEqual(PowerCycleVerdict.Restart, PowerCycle.Decide(json));
    }

    [TestMethod]
    public void A109AfterTheNewestStartIsIgnored()
    {
        // Only a 109 strictly before the newest start counts; one after it belongs to a later,
        // not-yet-relevant cycle and must not be read as this one's shutdown.
        string json = """
            {
                "kernelGeneral12":[{"utc":"2026-09-20T08:00:00Z"}],
                "kernelPower109":[{"utc":"2026-09-20T09:00:00Z","shutdownActionType":6}]
            }
            """;
        Assert.AreEqual(PowerCycleVerdict.Unknown, PowerCycle.Decide(json));
    }
}
