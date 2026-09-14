using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

[TestClass]
public sealed class ContextTests
{
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Create_DebugTrueInSettings_ResolvesDebugTrue()
    {
        var context = Context.Create(new TestSettings { Debug = true });

        Assert.IsTrue(context.Debug);
    }

    [TestMethod]
    public void Create_DebugOverrideTrue_ResolvesDebugTrueEvenWhenSettingsAreFalse()
    {
        var context = Context.Create(new TestSettings { Debug = false }, debugOverride: true);

        Assert.IsTrue(context.Debug);
    }

    [TestMethod]
    public void Create_NeitherSettingsNorOverride_ResolvesDebugFalse()
    {
        var context = Context.Create(new TestSettings { Debug = false });

        Assert.IsFalse(context.Debug);
    }

    [TestMethod]
    public void Create_NotDebug_DoesNotInstallDebugSink()
    {
        Context.Create(new TestSettings { Debug = false });

        // If a DebugLog had been installed, this would throw (its instance guard) -- it
        // shouldn't have been.
        var sink = DebugLog.Create();
        sink.Dispose();
    }

    [TestMethod]
    public void Create_Debug_InstallsDebugSinkAlongsideWhateverElseIsRegistered()
    {
        Context.Create(new TestSettings { Debug = true });

        // A DebugLog is already live from Create() above -- a second one should throw.
        Assert.ThrowsExactly<InvalidOperationException>(() => DebugLog.Create());
    }

    [TestMethod]
    public void Create_NoLogDir_DoesNotInstallFileSink()
    {
        Context.Create(new TestSettings { LogDir = null });

        // If a FileLog had been installed, this would throw (its instance guard) -- it shouldn't
        // have been.
        var logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var sink = FileLog.Create(logDir);
        sink.Dispose();
        Directory.Delete(logDir, recursive: true);
    }

    [TestMethod]
    public void Create_SettingsLogDir_InstallsFileSinkAlongsideWhateverElseIsRegistered()
    {
        var logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Context.Create(new TestSettings { LogDir = logDir });

            // A FileLog is already live from Create() above -- a second one should throw.
            Assert.ThrowsExactly<InvalidOperationException>(() => FileLog.Create(logDir));
        }
        finally
        {
            Logger.Reset();
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Create_LogDirOverride_TakesPrecedenceOverSettingsLogDir()
    {
        var settingsLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var overrideLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Context.Create(new TestSettings { LogDir = settingsLogDir }, logDirOverride: overrideLogDir);

            Assert.IsTrue(Directory.Exists(overrideLogDir));
            Assert.IsFalse(Directory.Exists(settingsLogDir));
        }
        finally
        {
            Logger.Reset();
            if (Directory.Exists(overrideLogDir))
            {
                Directory.Delete(overrideLogDir, recursive: true);
            }

            if (Directory.Exists(settingsLogDir))
            {
                Directory.Delete(settingsLogDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Current_BeforeCreate_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Context.Current);
    }

    [TestMethod]
    public void Current_AfterCreate_ReturnsTheCreatedInstance()
    {
        var context = Context.Create(new TestSettings { Debug = false });

        Assert.AreSame(context, Context.Current);
    }

    /// <summary>
    /// Proves Create() wires settings.json's log level/categories into the console sink (not just
    /// the debug sink) -- redirects Console.Out, which is process-wide with no per-context
    /// equivalent, so this can't run in parallel with anything else that also does.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Create_AppliesSettingsLogLevelToConsoleSink()
    {
        var settings = new TestSettings { LogLevel = TelemetryLevel.Warning };

        Context.Create(settings);

        var output = ConsoleCapture.CaptureOut(() =>
        {
            Logger.Info("quiet");
            Logger.Warning("audible");
        });

        Assert.DoesNotContain("quiet", output);
        Assert.Contains("audible", output);
    }

    [TestMethod]
    public void Start_RunsRunDelegateAndReturnsItsResult()
    {
        var console = new FakeConsole();

        var result = Context.Start(console, "test-app", NonExistentPath(), debugOverride: false, logDirOverride: null, () => 42);

        Assert.AreEqual(42, result);
    }

    [TestMethod]
    public void Start_LogDirOverride_ThreadsThroughToCreate()
    {
        var console = new FakeConsole();
        var logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Context.Start(console, "test-app", NonExistentPath(), debugOverride: false, logDirOverride: logDir, () => 0);

            Assert.IsTrue(Directory.Exists(logDir));
        }
        finally
        {
            Logger.Reset();
            if (Directory.Exists(logDir))
            {
                Directory.Delete(logDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Start_CallsEnsureConsoleAndReleaseConsole()
    {
        var console = new FakeConsole();

        Context.Start(console, "test-app", NonExistentPath(), debugOverride: false, logDirOverride: null, () => 0);

        Assert.AreEqual(1, console.EnsureConsoleCalls);
        Assert.AreEqual(1, console.ReleaseConsoleCalls);
    }

    [TestMethod]
    public void Start_SettingsLoadFails_ReturnsOneAndReleasesConsoleWithoutCallingRun()
    {
        var console = new FakeConsole();
        var malformedPath = WriteTempSettingsFile("not valid json");
        var runCalled = false;
        try
        {
            var result = Context.Start(console, "test-app", malformedPath, debugOverride: false, logDirOverride: null, () =>
            {
                runCalled = true;
                return 0;
            });

            Assert.AreEqual(1, result);
            Assert.AreEqual(1, console.ReleaseConsoleCalls);
            Assert.IsFalse(runCalled);
        }
        finally
        {
            File.Delete(malformedPath);
        }
    }

    [TestMethod]
    public void Start_RunThrowsAppErrorNotDebug_ReturnsOneAndReleasesConsole()
    {
        var console = new FakeConsole();

        var result = Context.Start(console, "test-app", NonExistentPath(), debugOverride: false, logDirOverride: null, () => throw new AppError("boom"));

        Assert.AreEqual(1, result);
        Assert.AreEqual(1, console.ReleaseConsoleCalls);
    }

    [TestMethod]
    public void Start_RunThrowsAppErrorAndDebug_RethrowsButStillReleasesConsole()
    {
        var console = new FakeConsole();

        Assert.ThrowsExactly<AppError>(() =>
            Context.Start(console, "test-app", NonExistentPath(), debugOverride: true, logDirOverride: null, () => throw new AppError("boom")));

        Assert.AreEqual(1, console.ReleaseConsoleCalls);
    }

    private static string NonExistentPath() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

    private static string WriteTempSettingsFile(string content)
    {
        var path = NonExistentPath();
        File.WriteAllText(path, content);
        return path;
    }
}
