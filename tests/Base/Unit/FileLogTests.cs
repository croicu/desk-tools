using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// Each test uses its own uniquely-named temp directory (deleted in a finally block, after the
/// sink under test is disposed so its file handle is already closed), so these run safely in
/// parallel with everything else -- no shared filesystem state, unlike ConsoleLogTests'/
/// DebugLogTests' Console.Out redirection.
/// </summary>
[TestClass]
public sealed class FileLogTests
{
    [TestMethod]
    public void Create_GuardsAgainstMultipleLiveInstances()
    {
        var logDir = NewTempDir();
        try
        {
            var sink1 = FileLog.Create(logDir);

            Assert.ThrowsExactly<InvalidOperationException>(() => FileLog.Create(logDir));

            sink1.Dispose();

            var sink2 = FileLog.Create(logDir);
            sink2.Dispose();
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Create_CreatesDirectoryIfMissing()
    {
        var logDir = NewTempDir();
        Assert.IsFalse(Directory.Exists(logDir));

        var sink = FileLog.Create(logDir);
        try
        {
            Assert.IsTrue(Directory.Exists(logDir));
        }
        finally
        {
            sink.Dispose();
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Log_WritesTimestampCorrelationLevelCategoryAndMessageToFile()
    {
        var logDir = NewTempDir();
        try
        {
            string line;
            var sink = FileLog.Create(logDir);
            try
            {
                sink.Log(TelemetryLevel.Warning, "heads up", "mycat");
            }
            finally
            {
                sink.Dispose();
            }

            line = ReadSoleLine(logDir);

            Assert.Contains($"[{Correlation.Current}]", line);
            Assert.Contains("[WARNING]", line);
            Assert.Contains("[mycat]", line);
            Assert.Contains("heads up", line);
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [TestMethod]
    public void Log_UnfilteredAtEveryLevel()
    {
        var logDir = NewTempDir();
        try
        {
            var sink = FileLog.Create(logDir);
            try
            {
                sink.Log(TelemetryLevel.Verbose, "chatty");
            }
            finally
            {
                sink.Dispose();
            }

            Assert.Contains("chatty", ReadSoleLine(logDir));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    /// <summary>
    /// Print() must not fall through to DiagnosticsLog's plain, unprefixed write -- unlike
    /// DebugLog (whose destination isn't shared across concurrent operations), a log file can
    /// interleave Print()'d lines from different operations, so even these need a
    /// timestamp/correlation prefix to be attributable -- just without [LEVEL][category], since
    /// Logger.Print() genuinely has neither.
    /// </summary>
    [TestMethod]
    public void Print_WritesTimestampAndCorrelationPrefixButNoLevelOrCategory()
    {
        var logDir = NewTempDir();
        try
        {
            var sink = FileLog.Create(logDir);
            string line;
            try
            {
                sink.Print("raw text");
            }
            finally
            {
                sink.Dispose();
            }

            line = ReadSoleLine(logDir);

            Assert.Contains($"[{Correlation.Current}]", line);
            Assert.EndsWith(" raw text", line);
            Assert.DoesNotContain("[INFO]", line);
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private static string ReadSoleLine(string logDir)
    {
        var files = Directory.GetFiles(logDir);
        Assert.HasCount(1, files);

        var lines = File.ReadAllLines(files[0]);
        Assert.HasCount(1, lines);

        return lines[0];
    }
}
