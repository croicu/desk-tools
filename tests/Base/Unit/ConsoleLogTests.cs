using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// ConsoleLog.Create()/Dispose()'s InstanceActive guard is AsyncLocal-scoped (see
/// Sinks/ConsoleLog.cs), and every test here passes its own StringWriter via Create()'s writer
/// parameter instead of redirecting the real, process-wide Console.Out (which no two tests
/// touching it at once could do without [DoNotParallelize]) -- so this class is fully safe to run
/// in parallel with everything else.
/// </summary>
[TestClass]
public sealed class ConsoleLogTests
{
    [TestMethod]
    public void Create_GuardsAgainstMultipleLiveInstances()
    {
        var sink1 = ConsoleLog.Create();

        Assert.ThrowsExactly<InvalidOperationException>(() => ConsoleLog.Create());

        sink1.Dispose();

        var sink2 = ConsoleLog.Create();
        sink2.Dispose();
    }

    [TestMethod]
    public void Log_BelowMinLevel_DoesNotPrint()
    {
        var writer = new StringWriter();
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Warning, writer: writer);
        try
        {
            sink.Log(TelemetryLevel.Info, "quiet");
            Assert.IsEmpty(writer.ToString());
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Log_AtOrAboveMinLevel_Prints()
    {
        var writer = new StringWriter();
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Warning, writer: writer);
        try
        {
            sink.Log(TelemetryLevel.Warning, "heads up", "mycat");
            Assert.AreEqual("[WARNING][mycat] heads up" + Environment.NewLine, writer.ToString());
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Log_ExplicitCategoryAllowList_FiltersUnlistedCategories()
    {
        var writer = new StringWriter();
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Verbose, categories: new List<string> { "allowed" }, writer: writer);
        try
        {
            sink.Log(TelemetryLevel.Info, "hidden", "other");
            sink.Log(TelemetryLevel.Info, "shown", "allowed");

            var output = writer.ToString();
            Assert.DoesNotContain("hidden", output);
            Assert.Contains("shown", output);
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Log_UnfilteredWithExcludedCategories_ActsAsDenyList()
    {
        var writer = new StringWriter();
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Verbose, excludedCategories: new List<string> { "noisy" }, writer: writer);
        try
        {
            sink.Log(TelemetryLevel.Info, "hidden", "noisy");
            sink.Log(TelemetryLevel.Info, "shown", DiagnosticsCategories.General);

            var output = writer.ToString();
            Assert.DoesNotContain("hidden", output);
            Assert.Contains("shown", output);
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Configure_UpdatesFilteringInPlace()
    {
        var writer = new StringWriter();
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Error, writer: writer);
        try
        {
            sink.Log(TelemetryLevel.Info, "still quiet");
            Assert.IsEmpty(writer.ToString());

            sink.Configure(minLevel: TelemetryLevel.Info);

            sink.Log(TelemetryLevel.Info, "now audible");
            Assert.Contains("now audible", writer.ToString());
        }
        finally
        {
            sink.Dispose();
        }
    }
}
