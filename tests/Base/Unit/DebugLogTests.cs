using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

[TestClass]
public sealed class DebugLogTests
{
    [TestMethod]
    public void Create_GuardsAgainstMultipleLiveInstances()
    {
        var sink1 = DebugLog.Create();

        Assert.ThrowsExactly<InvalidOperationException>(() => DebugLog.Create());

        sink1.Dispose();

        var sink2 = DebugLog.Create();
        sink2.Dispose();
    }

    [TestMethod]
    public void Log_WritesFormattedMessageToInjectedTarget()
    {
        var written = new List<string>();
        var sink = DebugLog.Create(written.Add);
        try
        {
            sink.Log(TelemetryLevel.Warning, "heads up", "mycat");
        }
        finally
        {
            sink.Dispose();
        }

        Assert.HasCount(1, written);
        Assert.AreEqual("[WARNING][mycat] heads up", written[0]);
    }

    [TestMethod]
    public void Log_UnfilteredAtEveryLevel()
    {
        var written = new List<string>();
        var sink = DebugLog.Create(written.Add);
        try
        {
            sink.Log(TelemetryLevel.Verbose, "chatty");
        }
        finally
        {
            sink.Dispose();
        }

        Assert.HasCount(1, written);
        Assert.Contains("chatty", written[0]);
    }

    /// <summary>
    /// Regression test: Print() used to fall through to the base DiagnosticsLog implementation,
    /// which writes straight to Console -- meaning a DebugLog fanned out alongside a "real" printing
    /// sink duplicated every Logger.Print() call onto stdout (caught via a live smoke test of Hello,
    /// where settings.debug=true made this concretely double-print every MCP response line).
    /// Redirects Console.Out to prove nothing reaches it -- process-wide, so no parallel siblings.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Print_RoutesToInjectedTargetNotConsole()
    {
        var written = new List<string>();
        var sink = DebugLog.Create(written.Add);
        try
        {
            var output = ConsoleCapture.CaptureOut(() => sink.Print("raw text"));

            Assert.IsEmpty(output);
            Assert.HasCount(1, written);
            Assert.AreEqual("raw text", written[0]);
        }
        finally
        {
            sink.Dispose();
        }
    }
}
