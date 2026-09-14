using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// ConsoleLog.Create()/Dispose()'s InstanceActive guard is now AsyncLocal-scoped (see
/// Diagnostics.cs), so it's no longer the reason this class avoids parallelism -- every test here
/// still redirects Console.Out/Error, though, which is a genuinely process-wide BCL property with
/// no per-context equivalent, so the class stays sequential to avoid two tests' captures racing.
/// </summary>
[TestClass]
[DoNotParallelize]
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
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Warning);
        try
        {
            var output = ConsoleCapture.CaptureOut(() => sink.Log(TelemetryLevel.Info, "quiet"));
            Assert.IsEmpty(output);
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Log_AtOrAboveMinLevel_Prints()
    {
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Warning);
        try
        {
            var output = ConsoleCapture.CaptureOut(() => sink.Log(TelemetryLevel.Warning, "heads up", "mycat"));
            Assert.AreEqual("[WARNING][mycat] heads up" + Environment.NewLine, output);
        }
        finally
        {
            sink.Dispose();
        }
    }

    [TestMethod]
    public void Log_ExplicitCategoryAllowList_FiltersUnlistedCategories()
    {
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Verbose, categories: new List<string> { "allowed" });
        try
        {
            var output = ConsoleCapture.CaptureOut(() =>
            {
                sink.Log(TelemetryLevel.Info, "hidden", "other");
                sink.Log(TelemetryLevel.Info, "shown", "allowed");
            });

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
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Verbose, excludedCategories: new List<string> { "noisy" });
        try
        {
            var output = ConsoleCapture.CaptureOut(() =>
            {
                sink.Log(TelemetryLevel.Info, "hidden", "noisy");
                sink.Log(TelemetryLevel.Info, "shown", DiagnosticsCategories.General);
            });

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
        var sink = ConsoleLog.Create(minLevel: TelemetryLevel.Error);
        try
        {
            var beforeOutput = ConsoleCapture.CaptureOut(() => sink.Log(TelemetryLevel.Info, "still quiet"));
            Assert.IsEmpty(beforeOutput);

            sink.Configure(minLevel: TelemetryLevel.Info);

            var afterOutput = ConsoleCapture.CaptureOut(() => sink.Log(TelemetryLevel.Info, "now audible"));
            Assert.Contains("now audible", afterOutput);
        }
        finally
        {
            sink.Dispose();
        }
    }

}
