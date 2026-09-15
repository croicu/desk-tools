using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Hello.Tests.Unit;

/// <summary>
/// Every test here installs its own DiagnosticsLog (the same sink Start() itself installs, see
/// src/Hello/Program.cs) pointed at a private StringWriter -- either directly (Run()-level tests,
/// via CaptureOutput()) or via Start()'s own injectable output parameter (Start()-level tests) --
/// instead of redirecting the real, process-wide Console.Out/Console.In, so this class runs safely
/// in parallel with everything else.
/// </summary>
[TestClass]
public sealed class ProgramTests
{
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Run_ValidRequest_LogsRawRequestLineAtInfoUnderMcpCategory()
    {
        const string request = """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""";
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        try
        {
            Program.Run(new StringReader(request));
        }
        finally
        {
            Logger.SetLogger(null);
        }

        Assert.IsTrue(sink.Received.Exists(r =>
            r.Level == TelemetryLevel.Info && r.Category == "mcp" && r.Message.Contains(request)));
    }

    /// <summary>
    /// Request logging goes through Logger.Info() (the normal Log() path, level/category-filtered
    /// per sink), not Logger.Print() (unconditional) -- unlike the response line, it must never
    /// leak to stdout regardless of which sink is active, since Hello never installs a ConsoleLog.
    /// This is exactly the same guarantee Run_EmptyInput_ReturnsZeroWithNoOutput already relies on
    /// for a request that logs nothing; this proves it still holds for one that does.
    /// </summary>
    [TestMethod]
    public void Run_ValidRequest_RequestLogNeverReachesStdout()
    {
        const string request = """{"jsonrpc":"2.0","id":8,"method":"tools/list"}""";

        var output = CaptureOutput(() => Program.Run(new StringReader(request)));

        Assert.DoesNotContain("received request", output);
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

    [TestMethod]
    public void ParseArgs_Log_SetsLogDir()
    {
        var arguments = Program.ParseArgs(["--log", "/var/log/hello"]);

        Assert.AreEqual("/var/log/hello", arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_NoArgs_LogDirIsNull()
    {
        var arguments = Program.ParseArgs([]);

        Assert.IsNull(arguments.LogDir);
    }

    [TestMethod]
    public void Run_EmptyInput_ReturnsZeroWithNoOutput()
    {
        var exitCode = -1;
        var output = CaptureOutput(() => exitCode = Program.Run(new StringReader(string.Empty)));

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(output);
    }

    [TestMethod]
    public void Run_Initialize_RespondsWithProtocolVersionAndServerInfo()
    {
        const string request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";

        var response = RunSingleRequest(request);

        Assert.AreEqual("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.AreEqual("hello", response.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.IsTrue(response.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [TestMethod]
    public void Run_ToolsList_ReturnsSayHelloAndStopTools()
    {
        const string request = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

        var response = RunSingleRequest(request);

        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var toolNames = new List<string?>();
        foreach (var tool in tools)
        {
            toolNames.Add(tool.GetProperty("name").GetString());
        }

        Assert.HasCount(2, tools);
        CollectionAssert.AreEquivalent(new List<string?> { "say_hello", "stop" }, toolNames);
    }

    [TestMethod]
    public void Run_ToolsCallSayHello_ReturnsHiText()
    {
        const string request = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"say_hello","arguments":{}}}""";

        var response = RunSingleRequest(request);

        var result = response.RootElement.GetProperty("result");
        Assert.IsFalse(result.GetProperty("isError").GetBoolean());
        var content = result.GetProperty("content").EnumerateArray().ToList();
        Assert.AreEqual("Hi from MCP", content[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public void Run_ToolsCallUnknownTool_ReturnsInvalidParamsError()
    {
        const string request = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"not_a_real_tool","arguments":{}}}""";

        var response = RunSingleRequest(request);

        Assert.AreEqual(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void Run_ToolsCallStop_RespondsSuccessfully()
    {
        const string request = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"stop","arguments":{}}}""";

        var response = RunSingleRequest(request);

        Assert.IsFalse(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    /// <summary>
    /// The whole point of the stop tool: it must respond first, then stop the read loop the same
    /// way EOF would -- a request sent after it must never be handled, proving Run() actually broke
    /// out rather than continuing to read.
    /// </summary>
    [TestMethod]
    public void Run_ToolsCallStop_StopsReadLoopBeforeALaterRequest()
    {
        const string input = """
            {"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"stop","arguments":{}}}
            {"jsonrpc":"2.0","id":10,"method":"tools/list"}
            """;

        var output = CaptureOutput(() => Program.Run(new StringReader(input)));

        var lines = new List<string>();
        using (var reader = new StringReader(output))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        Assert.HasCount(1, lines);
        var response = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(9, response.RootElement.GetProperty("id").GetInt32());
    }

    [TestMethod]
    public void Run_UnknownMethod_ReturnsMethodNotFoundError()
    {
        const string request = """{"jsonrpc":"2.0","id":5,"method":"not/a/real/method"}""";

        var response = RunSingleRequest(request);

        Assert.AreEqual(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void Run_MalformedJson_RespondsWithParseErrorAndNullId()
    {
        var response = RunSingleRequest("not valid json at all");

        Assert.AreEqual(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    [TestMethod]
    public void Run_NotificationsInitialized_ProducesNoResponse()
    {
        const string notification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

        var output = CaptureOutput(() => Program.Run(new StringReader(notification)));

        Assert.IsEmpty(output);
    }

    [TestMethod]
    public void Run_NotificationFollowedByRequest_StillAnswersTheRequest()
    {
        const string input = """
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":6,"method":"tools/list"}
            """;

        var response = RunSingleRequest(input);

        Assert.IsTrue(response.RootElement.GetProperty("result").TryGetProperty("tools", out _));
    }

    /// <summary>
    /// Proves the critical protocol-safety guarantee Start() exists to preserve: Context.Start's
    /// internal Settings.Load() call always emits its own Logger.Diagnostic/Warning/Info calls (a
    /// nonexistent settingsPath guarantees at least one -- "no settings file found ...; falling
    /// back to restrictive defaults"). If Start() didn't install a silent sink first, those would
    /// reach Logger's default ConsoleLog sink and print straight to stdout alongside the real
    /// response, corrupting the protocol stream. Uses Start()'s own injectable input/output
    /// parameters rather than redirecting the real Console.In/Console.Out.
    /// </summary>
    [TestMethod]
    public void Start_EmptyInput_ProducesNoOutputDespiteSettingsLoadLoggingInternally()
    {
        var writer = new StringWriter();
        var exitCode = Program.Start(settingsPath: NonExistentPath(), input: new StringReader(string.Empty), output: writer);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(writer.ToString());
    }

    /// <summary>
    /// End-to-end through the real entry point: Start() wires Context/Settings, then Run() serves
    /// the request -- exactly one clean response line, no diagnostic noise mixed in.
    /// </summary>
    [TestMethod]
    public void Start_InitializeRequest_RespondsCleanly()
    {
        const string request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var writer = new StringWriter();

        Program.Start(settingsPath: NonExistentPath(), input: new StringReader(request), output: writer);

        var response = ParseSingleResponseLine(writer.ToString());
        Assert.AreEqual("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    /// <summary>
    /// Proves --log's whole reason for existing: a stdio server can never print diagnostics to its
    /// own console, so a persisted log file is the one way to see what happened -- and installing it
    /// must not disturb the clean, single-line protocol response on stdout.
    /// </summary>
    [TestMethod]
    public void Start_WithLogArgument_WritesLogFileAndStillRespondsCleanly()
    {
        var logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        const string request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var writer = new StringWriter();
        try
        {
            Program.Start(["--log", logDir], settingsPath: NonExistentPath(), input: new StringReader(request), output: writer);

            var output = writer.ToString();
            var response = ParseSingleResponseLine(output);
            Assert.AreEqual("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.IsTrue(Directory.Exists(logDir));
            var logFiles = Directory.GetFiles(logDir);
            Assert.HasCount(1, logFiles);

            // FileLog's own file handle is still open at this point (Context.Start never disposes
            // sinks after run() returns) -- reset first so File.ReadAllText below doesn't race a
            // still-open write handle.
            Logger.Reset();

            var logContent = File.ReadAllText(logFiles[0]);
            Assert.Contains("received request", logContent);
            Assert.Contains(request, logContent);
            Assert.Contains(output.Trim(), logContent);
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

    /// <summary>
    /// Installs the same DiagnosticsLog sink Start() itself installs (see src/Hello/Program.cs),
    /// pointed at a private StringWriter instead of the real Console.Out, runs <paramref
    /// name="action"/>, then returns what was written -- the Run()-level equivalent of Start()'s own
    /// injectable output parameter, for tests that call Program.Run() directly rather than going
    /// through Start().
    /// </summary>
    private static string CaptureOutput(Action action)
    {
        var writer = new StringWriter();
        Logger.SetLogger(new DiagnosticsLog(printWriter: writer));
        try
        {
            action();
        }
        finally
        {
            Logger.SetLogger(null);
        }

        return writer.ToString();
    }

    /// <summary>
    /// Runs Program.Run() over the given newline-delimited input and asserts it produced exactly
    /// one response line, returning it parsed.
    /// </summary>
    private static JsonDocument RunSingleRequest(string input)
    {
        var output = CaptureOutput(() => Program.Run(new StringReader(input)));
        return ParseSingleResponseLine(output);
    }

    private static string NonExistentPath() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

    /// <summary>
    /// Not disposed -- the JsonDocument only needs to outlive its short-lived caller, not be held
    /// long-term.
    /// </summary>
    private static JsonDocument ParseSingleResponseLine(string output)
    {
        var lines = new List<string>();
        using (var reader = new StringReader(output))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        Assert.HasCount(1, lines);
        return JsonDocument.Parse(lines[0]);
    }
}
