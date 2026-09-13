using __package_name__.Base;

namespace __package_name__.Base.Tests.Unit;

[TestClass]
public sealed class ErrorsTests
{
    /// <summary>
    /// AppError/TaskError log through Logger.Log(), which lazily creates Logger's default
    /// ConsoleLogSink (see Diagnostics.cs) if nothing else has already; nothing here ever disposes
    /// it, so reset after each test to avoid leaving a live default that could make some other
    /// test's own ConsoleLogSink.Create() throw.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void AppError_RecordCapturesWarningLevelAndMessage()
    {
        var error = new TaskError("something went wrong");

        Assert.AreEqual("something went wrong", error.Message);
        Assert.AreEqual(TelemetryLevel.Warning, error.Record.Level);
        Assert.AreEqual("something went wrong", error.Record.Message);
        Assert.AreEqual(DiagnosticsCategories.General, error.Record.Category);
    }

    [TestMethod]
    public void TaskError_IsAnAppError()
    {
        var error = new TaskError("oops");

        Assert.IsInstanceOfType(error, typeof(AppError));
    }

    [TestMethod]
    public void SettingsError_RecordCapturesSettingsCategory()
    {
        var error = new SettingsError("bad settings.json");

        Assert.AreEqual(TelemetryLevel.Warning, error.Record.Level);
        Assert.AreEqual("bad settings.json", error.Record.Message);
        Assert.AreEqual(DiagnosticsCategories.Settings, error.Record.Category);
    }

    [TestMethod]
    public void SettingsError_IsAnAppError()
    {
        var error = new SettingsError("oops");

        Assert.IsInstanceOfType(error, typeof(AppError));
    }

    /// <summary>
    /// Logger.Flush()/Clear() touch DiagnosticsLogSink's Pending buffer, but that buffer is now
    /// AsyncLocal-scoped (see Diagnostics.cs) rather than a process-wide static, so this test's own
    /// records are isolated from whatever an unrelated concurrently-running test logs.
    /// </summary>
    [TestMethod]
    public void TelemetrySession_DisposeFlushesAndClearsPendingBuffer()
    {
        Logger.Drain();

        Logger.Log(TelemetryLevel.Info, "should be cleared by session disposal");

        using (new TelemetrySession())
        {
        }

        var remaining = Logger.Drain();
        Assert.IsEmpty(remaining);
    }
}
