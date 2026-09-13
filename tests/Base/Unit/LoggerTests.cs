using __package_name__.Base;
using __package_name__.Mocks;

namespace __package_name__.Base.Tests.Unit;

/// <summary>
/// Logger.SetLogger mutates the sink stack, but that stack is now AsyncLocal-scoped (see
/// Diagnostics.cs) rather than a process-wide static, so each test's own push/pop is isolated to
/// its own logical call context and this class is safe to run in parallel with everything else.
/// </summary>
[TestClass]
public sealed class LoggerTests
{
    /// <summary>
    /// Every call here that doesn't push its own sink lazily creates Logger's default
    /// ConsoleLogSink (see Diagnostics.cs), which nothing here ever disposes -- reset after each
    /// test so a leftover live default can't make some other test's own ConsoleLogSink.Create()
    /// throw, regardless of whether the test runner happens to reuse this logical call context.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Log_ReturnsRecordWithGivenLevelMessageCategory()
    {
        var record = Logger.Log(TelemetryLevel.Warning, "hello", "custom");

        Assert.AreEqual(TelemetryLevel.Warning, record.Level);
        Assert.AreEqual("hello", record.Message);
        Assert.AreEqual("custom", record.Category);
    }

    [TestMethod]
    public void Log_DefaultsCategoryToGeneral()
    {
        var record = Logger.Log(TelemetryLevel.Info, "hello");

        Assert.AreEqual(DiagnosticsCategories.General, record.Category);
    }

    /// <summary>
    /// Proves Logger works with zero setup -- no ConsoleLogSink.Create()/SetLogger()/
    /// ConfigureConsole() call required to get correct output at the bootstrap defaults (Error
    /// shows, Info/Warning are suppressed, categories are unfiltered so any category can show).
    /// Redirects Console.Out, which is process-wide with no per-context equivalent, so this one
    /// method (unlike the rest of this class) can't run in parallel with anything else.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Log_WithNoSetup_UsesBootstrapConsoleDefaults()
    {
        var output = ConsoleCapture.CaptureOut(() =>
        {
            Logger.Info("should be suppressed");
            Logger.Warning("should also be suppressed");
            Logger.Error("should print", "some-other-category");
        });

        Assert.DoesNotContain("should be suppressed", output);
        Assert.DoesNotContain("should also be suppressed", output);
        Assert.Contains("should print", output);
    }

    /// <summary>
    /// Calling SetLogger before anything else touches Logger in this context should install
    /// `sink` directly, not lazily create the default ConsoleLogSink first and shadow it -- if the
    /// default had been created too, it would still hold ConsoleLogSink's guard, and the
    /// Create() call below would throw.
    /// </summary>
    [TestMethod]
    public void SetLogger_AsFirstCall_DoesNotCreateDefaultConsoleLogSink()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            var consoleSink = ConsoleLogSink.Create();
            consoleSink.Dispose();
        }
        finally
        {
            Logger.SetLogger(null);
        }
    }

    [TestMethod]
    public void SetLogger_RoutesSubsequentCallsToPushedSinkUntilPopped()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Info("hello", "cat");
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Info, sink.Received[0].Level);
        Assert.AreEqual("hello", sink.Received[0].Message);
        Assert.AreEqual("cat", sink.Received[0].Category);

        Logger.Info("after pop, should not reach the popped sink");
        Assert.HasCount(1, sink.Received);
    }

    [TestMethod]
    public void Warning_LogsAtWarningLevel()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Warning("careful");
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Warning, sink.Received[0].Level);
    }

    [TestMethod]
    public void Error_LogsAtErrorLevel()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Error("broken");
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Error, sink.Received[0].Level);
    }

    [TestMethod]
    public void Fatal_LogsAtCriticalLevel()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Fatal("unrecoverable");
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Critical, sink.Received[0].Level);
    }

    [TestMethod]
    public void Diagnostic_LogsAtVerboseLevel()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Diagnostic("progress");
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Verbose, sink.Received[0].Level);
    }

    [TestMethod]
    public void Perf_LogsAtInfoUnderPerfCategoryWithExpectedMessageShape()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Logger.Perf("network call", 0.5);
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink.Received);
        Assert.AreEqual(TelemetryLevel.Info, sink.Received[0].Level);
        Assert.AreEqual(DiagnosticsCategories.Perf, sink.Received[0].Category);
        Assert.AreEqual("duration: 0.500s - network call", sink.Received[0].Message);
    }

    private sealed class RecordingSink : DiagnosticsLogSink
    {
        public List<TelemetryRecord> Received { get; } = new();

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            var record = base.Log(level, message, category);
            Received.Add(record);
            return record;
        }
    }
}
