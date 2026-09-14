using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Hello.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class ProgramTests
{
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Run_EmptyInput_ReturnsZeroWithNoOutput()
    {
        var exitCode = -1;
        var output = ConsoleCapture.CaptureOut(() => exitCode = Program.Run(new StringReader(string.Empty)));

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
    public void Run_ToolsList_ReturnsSayHelloTool()
    {
        const string request = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

        var response = RunSingleRequest(request);

        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.HasCount(1, tools);
        Assert.AreEqual("say_hello", tools[0].GetProperty("name").GetString());
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

        var output = ConsoleCapture.CaptureOut(() => Program.Run(new StringReader(notification)));

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
    /// response, corrupting the protocol stream. Redirects Console.In (real stdin) in addition to
    /// the class's usual Console.Out capture, since Start() -- unlike Run() -- doesn't take an
    /// injectable TextReader.
    /// </summary>
    [TestMethod]
    public void Start_EmptyInput_ProducesNoOutputDespiteSettingsLoadLoggingInternally()
    {
        var originalIn = Console.In;
        Console.SetIn(new StringReader(string.Empty));
        try
        {
            var exitCode = -1;
            var output = ConsoleCapture.CaptureOut(() => exitCode = Program.Start(settingsPath: NonExistentPath()));

            Assert.AreEqual(0, exitCode);
            Assert.IsEmpty(output);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// End-to-end through the real entry point: Start() wires Context/Settings, then Run() serves
    /// the request -- exactly one clean response line, no diagnostic noise mixed in.
    /// </summary>
    [TestMethod]
    public void Start_InitializeRequest_RespondsCleanly()
    {
        const string request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var originalIn = Console.In;
        Console.SetIn(new StringReader(request));
        try
        {
            var output = ConsoleCapture.CaptureOut(() => Program.Start(settingsPath: NonExistentPath()));

            var response = ParseSingleResponseLine(output);
            Assert.AreEqual("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    /// <summary>
    /// Runs Program.Run() over the given newline-delimited input and asserts it produced exactly
    /// one response line, returning it parsed.
    /// </summary>
    private static JsonDocument RunSingleRequest(string input)
    {
        var output = ConsoleCapture.CaptureOut(() => Program.Run(new StringReader(input)));
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
