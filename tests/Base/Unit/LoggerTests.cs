using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// Logger.SetLogger mutates the sink list, but that list is AsyncLocal-scoped (see Logger.cs)
/// rather than a process-wide static, so each test's own push/pop is isolated to its own logical
/// call context and this class is safe to run in parallel with everything else -- including the
/// two tests that used to need [DoNotParallelize] for redirecting the real Console.Out, now that
/// they inject their own writer instead (see ConsoleLog.cs's DefaultWriterOverride and Create()'s
/// writer parameter).
/// </summary>
[TestClass]
public sealed class LoggerTests
{
    /// <summary>
    /// Every call here that doesn't push its own sink lazily creates Logger's default
    /// ConsoleLog (see Logger.cs), which nothing here ever disposes -- reset after each
    /// test so a leftover live default can't make some other test's own ConsoleLog.Create()
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
    /// Proves Logger works with zero setup -- no ConsoleLog.Create()/SetLogger()/
    /// ConfigureConsole() call required to get correct output at the bootstrap defaults (Error
    /// shows, Info/Warning are suppressed, categories are unfiltered so any category can show).
    /// Exercises Logger's own lazily-created bootstrap sink (see Logger.cs's ActiveSinks()), which
    /// calls ConsoleLog.Create() with no writer of its own to pass -- ConsoleLog.DefaultWriterOverride
    /// lets this test observe that lazily-created sink's output without redirecting the real,
    /// process-wide Console.Out, so (unlike before) this runs safely in parallel with everything
    /// else.
    /// </summary>
    [TestMethod]
    public void Log_WithNoSetup_UsesBootstrapConsoleDefaults()
    {
        var writer = new StringWriter();
        ConsoleLog.DefaultWriterOverride.Value = writer;
        try
        {
            Logger.Info("should be suppressed");
            Logger.Warning("should also be suppressed");
            Logger.Error("should print", "some-other-category");
        }
        finally
        {
            ConsoleLog.DefaultWriterOverride.Value = null;
        }

        var output = writer.ToString();
        Assert.DoesNotContain("should be suppressed", output);
        Assert.DoesNotContain("should also be suppressed", output);
        Assert.Contains("should print", output);
    }

    /// <summary>
    /// Calling SetLogger before anything else touches Logger in this context should install
    /// `sink` directly, not lazily create the default ConsoleLog first and shadow it -- if the
    /// default had been created too, it would still hold ConsoleLog's guard, and the
    /// Create() call below would throw.
    /// </summary>
    [TestMethod]
    public void SetLogger_AsFirstCall_DoesNotCreateDefaultConsoleLog()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            var consoleSink = ConsoleLog.Create();
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

    [TestMethod]
    public void MultipleSinks_AllReceiveEachLogCall()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        Logger.SetLogger(sink1);
        Logger.SetLogger(sink2);
        try
        {
            Logger.Info("hello", "cat");
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }

        Assert.HasCount(1, sink1.Received);
        Assert.HasCount(1, sink2.Received);
        Assert.AreEqual("hello", sink1.Received[0].Message);
        Assert.AreEqual("hello", sink2.Received[0].Message);
    }

    [TestMethod]
    public void MultipleSinks_InvokedSequentiallyInRegistrationOrder()
    {
        var order = new List<string>();
        var sink1 = new OrderTrackingSink("first", order);
        var sink2 = new OrderTrackingSink("second", order);
        Logger.SetLogger(sink1);
        Logger.SetLogger(sink2);
        try
        {
            Logger.Info("hello");
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }

        CollectionAssert.AreEqual(new List<string> { "first", "second" }, order);
    }

    [TestMethod]
    public void Log_WithMultipleSinks_ReturnsFirstRegisteredSinksRecord()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        Logger.SetLogger(sink1);
        Logger.SetLogger(sink2);
        try
        {
            var record = Logger.Log(TelemetryLevel.Warning, "hello", "cat");

            Assert.AreSame(sink1.Received[0], record);
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }
    }

    [TestMethod]
    public void Drain_WithMultipleSinks_ConcatenatesEachSinksOwnMessages()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        Logger.SetLogger(sink1);
        Logger.SetLogger(sink2);
        try
        {
            Logger.Info("hello");

            var messages = Logger.Drain();

            CollectionAssert.AreEqual(new List<string> { "hello", "hello" }, messages);
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }
    }

    [TestMethod]
    public void Clear_WithMultipleSinks_ClearsEachSinksOwnBufferIndependently()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        Logger.SetLogger(sink1);
        Logger.SetLogger(sink2);
        try
        {
            Logger.Info("hello");
            Logger.Clear();

            Assert.IsEmpty(Logger.Drain());
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }
    }

    /// <summary>
    /// Regression test for a real bug caught via a live smoke test of Hello with settings.debug=true:
    /// DebugLog didn't override Print(), so it inherited DiagnosticsLog's write-straight-to-Console
    /// behavior -- meaning Logger.Print() with a ConsoleLog and a DebugLog both active (the normal
    /// debug-mode shape) wrote the same text to stdout twice. Both sinks are constructed with their
    /// own injected writer, so this runs safely in parallel with everything else instead of needing
    /// to redirect the real, process-wide Console.Out.
    /// </summary>
    [TestMethod]
    public void Print_WithConsoleAndDebugSinksBothActive_WritesToConsoleExactlyOnce()
    {
        var consoleWriter = new StringWriter();
        var consoleSink = ConsoleLog.Create(writer: consoleWriter);
        var debugWritten = new List<string>();
        var debugSink = DebugLog.Create(debugWritten.Add);
        Logger.SetLogger(consoleSink);
        Logger.SetLogger(debugSink);
        try
        {
            Logger.Print("usage: desk-tools [--debug]");

            Assert.AreEqual("usage: desk-tools [--debug]" + Environment.NewLine, consoleWriter.ToString());
            Assert.HasCount(1, debugWritten);
            Assert.AreEqual("usage: desk-tools [--debug]", debugWritten[0]);
        }
        finally
        {
            Logger.SetLogger(null);
            Logger.SetLogger(null);
        }
    }

    private sealed class RecordingSink : DiagnosticsLog
    {
        public List<TelemetryRecord> Received { get; } = new();

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            var record = base.Log(level, message, category);
            Received.Add(record);
            return record;
        }
    }

    private sealed class OrderTrackingSink : DiagnosticsLog
    {
        private readonly string _name;
        private readonly List<string> _order;

        public OrderTrackingSink(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            _order.Add(_name);
            return base.Log(level, message, category);
        }
    }
}
