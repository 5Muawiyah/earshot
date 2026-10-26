using Earshot.App;
using Earshot.Boot;
using Earshot.Infra;
using Earshot.Repair;
using Earshot.Tests.Integration.Coordinator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// What the tray gives its coordinator about Fix connection: a real answer to a run that stopped half way, the real window of a
// re-pair and the real check for AirPods that are not the protected ones; and nothing of the three where the gate is never
// reached. Building them touches no device, task or file.
[TestClass]
public sealed class TrayFixServicesTests
{
    private static CoordinatorServices Build(Paths paths, ISystemWorker? worker) =>
        Program.CreateCoordinatorServices(paths, new FakeSettings(), new CapturingLog(), null, TimeProvider.System, () => null, worker);

    [TestMethod]
    public void AnInstalledStyleTrayGetsTheThreeRealServicesAndTheCardsButton()
    {
        using var worker = new SystemWorker(new CapturingLog());

        CoordinatorServices services = Build(Paths.FromEnvironment(_ => null), worker);

        Assert.IsInstanceOfType<FixRecoveryAtStart>(services.Recovery);
        Assert.IsInstanceOfType<FixMarkerRePairWindow>(services.RePair);
        Assert.IsInstanceOfType<AirPodsProtectionCheck>(services.Protection);
        Assert.IsInstanceOfType<OwnProgramFixLauncher>(services.FixLauncher);
        Assert.IsInstanceOfType<NodePairedAirPodsCheck>(services.PairedAirPods);
    }

    // The coordinator's checks for unprotected AirPods wait while the tray pairs or recovers a pairing, which they can only see
    // when both hold the same flag.
    [TestMethod]
    public void ThePairingServicesShareTheCoordinatorsRunFlag()
    {
        Paths paths = Paths.FromEnvironment(_ => null);
        CoordinatorServices services = Build(paths, null);

        PairAirPodsServices? pairing = Program.PairAirPodsFor(paths, new CapturingLog(), services);

        Assert.IsNotNull(pairing);
        Assert.AreSame(services.TrayPairing, pairing.RunFlag);
        Assert.IsNull(Program.PairAirPodsFor(Paths.FromEnvironment(name => name == Paths.SafeModeVariable ? "1" : null), new CapturingLog(), services));
    }

    [TestMethod]
    public void TheReadOnlyPairedCheckIsRealEvenWhereTheGateIsNeverReached()
    {
        var safe = Paths.FromEnvironment(name => name == Paths.SafeModeVariable ? "1" : null);
        var redirected = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? Path.GetTempPath() : null);

        Assert.IsInstanceOfType<NodePairedAirPodsCheck>(Build(safe, null).PairedAirPods);
        Assert.IsInstanceOfType<NodePairedAirPodsCheck>(Build(redirected, null).PairedAirPods);
    }

    [TestMethod]
    public void SafeModeAndARedirectedDataRootGetNoneOfThem()
    {
        using var worker = new SystemWorker(new CapturingLog());
        var safe = Paths.FromEnvironment(name => name == Paths.SafeModeVariable ? "1" : null);
        var redirected = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? Path.GetTempPath() : null);

        foreach (Paths paths in new[] { safe, redirected })
        {
            CoordinatorServices services = Build(paths, worker);

            Assert.AreSame(NoStartRecovery.Instance, services.Recovery);
            Assert.AreSame(NoRePairWindow.Instance, services.RePair);
            Assert.AreSame(NoProtectionCheck.Instance, services.Protection);
        }
    }

    [TestMethod]
    public void TheTraceRecorderIsRealOnlyWhereTheHelperCouldRun()
    {
        var safe = Paths.FromEnvironment(name => name == Paths.SafeModeVariable ? "1" : null);
        var redirected = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? Path.GetTempPath() : null);

        Assert.IsNull(Program.CreateTraceRecorder(safe, new CapturingLog()), "The helper refuses safe mode.");
        Assert.IsNull(Program.CreateTraceRecorder(redirected, new CapturingLog()), "The helper refuses a redirected data folder.");
        using Earshot.Tracing.ITraceRecorder? real = Program.CreateTraceRecorder(Paths.FromEnvironment(_ => null), new CapturingLog());
        Assert.IsInstanceOfType<Earshot.Tracing.TraceRecorder>(real);
        Assert.AreEqual(Earshot.Tracing.TraceState.Idle, real.State, "Making it starts nothing.");
    }

    [TestMethod]
    public void WithoutASystemWorkerThereIsNothingToAnswerWith()
    {
        CoordinatorServices services = Build(Paths.FromEnvironment(_ => null), null);

        Assert.AreSame(NoStartRecovery.Instance, services.Recovery);
        Assert.IsInstanceOfType<FixMarkerRePairWindow>(services.RePair);
    }
}
