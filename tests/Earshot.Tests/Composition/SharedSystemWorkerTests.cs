using System.Text.RegularExpressions;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Composition;

// The tray's gate requests all go on one thread. Building the registry starts no thread and makes no native
// call: the system worker's thread starts on its first item, and the controllers only read the current user's
// SID while they are built.
[TestClass]
public sealed partial class SharedSystemWorkerTests
{
    private static ServiceRegistry Build(TempFolder temp, ILog log) =>
        CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), static action => action(), safeMode: true);

    [TestMethod]
    public void TheBlockAndProtectionControllersRunOnTheSameWorker()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        ServiceRegistry registry = Build(temp, log);

        try
        {
            Assert.IsNotNull(registry.SystemWorker, "The boot hook leaves its worker on the registry.");
            var block = (BlockController)((SafeBlockController)registry.Block).Inner;
            var protection = (AudioProtectionController)((SafeAudioProtectionController)registry.Protection).Inner;

            Assert.AreSame(registry.SystemWorker, block.Worker);
            Assert.AreSame(registry.SystemWorker, protection.Worker);
            Assert.IsInstanceOfType<SystemWorker>(registry.SystemWorker);
            Assert.IsNull(((SystemWorker)registry.SystemWorker).ThreadId, "No thread starts while the services are built.");
        }
        finally
        {
            Release(registry);
        }
    }

    // Disposing the registry's worker is enough: neither controller owns one when they share it.
    [TestMethod]
    public void NeitherControllerOwnsTheSharedWorker()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        ServiceRegistry registry = Build(temp, log);
        registry.Monitor.Dispose();
        registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var worker = (SystemWorker)registry.SystemWorker!;
        var block = (BlockController)((SafeBlockController)registry.Block).Inner;
        var protection = (AudioProtectionController)((SafeAudioProtectionController)registry.Protection).Inner;

        block.Dispose();
        protection.Dispose();

        Assert.IsNotNull(worker.RunAsync(_ => 1), "The shared worker is still usable after both controllers are disposed.");
        Assert.AreEqual(1, worker.RunAsync(_ => 1).GetAwaiter().GetResult());
        worker.Dispose();
    }

    // Nothing in the application may reach past a safe-mode decorator to the controller it wraps: that would
    // run a live device action in a run that refuses them. Only the tests unwrap.
    [TestMethod]
    public void NoProductionCodeReachesInnerPastASafeDecorator()
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
                if (InnerAccess().IsMatch(lines[i]) && !Path.GetFileName(file).Equals("SafeDecorators.cs", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(src, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                }
            }
        }

        Assert.IsEmpty(offenders, string.Join(Environment.NewLine, offenders));
    }

    // Any use of the name, so a property pattern ({ Inner: ... }) or nameof counts as well as member access.
    [GeneratedRegex(@"\bInner\b")]
    private static partial Regex InnerAccess();

    private static void Release(ServiceRegistry registry)
    {
        ((IDisposable)((SafeBlockController)registry.Block).Inner).Dispose();
        ((IDisposable)((SafeAudioProtectionController)registry.Protection).Inner).Dispose();
        (registry.SystemWorker as IDisposable)?.Dispose();
        registry.Monitor.Dispose();
        registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

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
