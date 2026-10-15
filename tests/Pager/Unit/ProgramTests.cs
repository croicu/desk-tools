using System.Net;
using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Pager.Tests.Unit;

/// <summary>
/// The MCP surface, end to end through the real entry point (<see cref="Program.Start"/>): requests go in
/// through an injected reader, responses come out of an injected writer, and the network is a fake handler.
/// Nothing touches the process-wide console, so the class runs in parallel with everything else.
/// </summary>
[TestClass]
public sealed class ProgramTests
{
    private const string Topic = "s3cret-topic-xyz";

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private sealed class CountingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private static string? ConfiguredEnvironment(string name)
    {
        return name == PagerConfig.TopicVariable ? Topic : null;
    }

    private static string? EmptyEnvironment(string name)
    {
        return null;
    }

    private static string NonExistentPath()
    {
        return Path.Combine(Path.GetTempPath(), $"pager-tests-{Guid.NewGuid():N}.json");
    }

    /// <summary>Feeds <paramref name="requests"/> (one JSON-RPC message per line) to the server and returns each line it wrote.</summary>
    private static List<JsonDocument> Serve(Func<string, string?> environment, HttpMessageHandler? handler, params string[] requests)
    {
        var output = new StringWriter();
        var exitCode = Program.Start(
            settingsPath: NonExistentPath(),
            input: new StringReader(string.Join("\n", requests)),
            output: output,
            getEnvironmentVariable: environment,
            handler: handler);
        Assert.AreEqual(0, exitCode);

        var responses = new List<JsonDocument>();
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            responses.Add(JsonDocument.Parse(line));
        }

        return responses;
    }

    private static JsonElement Single(List<JsonDocument> responses)
    {
        Assert.HasCount(1, responses);
        return responses[0].RootElement;
    }

    private static string CallPage(string arguments)
    {
        return $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"page","arguments":{{{arguments}}}}}""";
    }

    [TestMethod]
    public void Start_EmptyInput_ProducesNoOutputDespiteSettingsLoadLoggingInternally()
    {
        Assert.IsEmpty(Serve(ConfiguredEnvironment, new CountingHandler()));
    }

    [TestMethod]
    public void Initialize_AnnouncesAToolsServerNamedPager()
    {
        var response = Single(Serve(ConfiguredEnvironment, new CountingHandler(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""));

        var result = response.GetProperty("result");
        Assert.AreEqual("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.AreEqual("pager", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.IsTrue(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [TestMethod]
    public void ToolsList_HasOnePageToolRequiringAMessage()
    {
        var response = Single(Serve(ConfiguredEnvironment, new CountingHandler(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var tools = response.GetProperty("result").GetProperty("tools");
        Assert.AreEqual(1, tools.GetArrayLength());
        Assert.AreEqual("page", tools[0].GetProperty("name").GetString());
        Assert.AreEqual("message", tools[0].GetProperty("inputSchema").GetProperty("required")[0].GetString());
    }

    [TestMethod]
    public void Notification_GetsNoResponse()
    {
        Assert.IsEmpty(Serve(ConfiguredEnvironment, new CountingHandler(), """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [TestMethod]
    public void Page_Configured_SendsAndSaysSo()
    {
        var handler = new CountingHandler();

        var response = Single(Serve(ConfiguredEnvironment, handler, CallPage("""{"message":"Build finished"}""")));

        var result = response.GetProperty("result");
        Assert.IsFalse(result.GetProperty("isError").GetBoolean());
        Assert.AreEqual("Notification sent.", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public void Page_NotConfigured_StillStartsAndAnswersNotConfigured()
    {
        var handler = new CountingHandler();

        var response = Single(Serve(EmptyEnvironment, handler, CallPage("""{"message":"hello"}""")));

        var result = response.GetProperty("result");
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        StringAssert.Contains(text, "not configured");
        StringAssert.Contains(text, "NTFY_TOPIC");
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public void Page_ServerRefuses_IsAToolErrorNotAProtocolError()
    {
        var handler = new CountingHandler { Status = HttpStatusCode.Forbidden };

        var response = Single(Serve(ConfiguredEnvironment, handler, CallPage("""{"message":"hello"}""")));

        Assert.IsFalse(response.TryGetProperty("error", out _));
        Assert.IsTrue(response.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"message":""}""")]
    [DataRow("""{"message":5}""")]
    [DataRow("""{"message":"x","priority":9}""")]
    [DataRow("""{"message":"x","priority":"high"}""")]
    [DataRow("""{"message":"x","title":3}""")]
    [DataRow("""{"message":"x","tags":"a"}""")]
    [DataRow("""{"message":"x","tags":[1]}""")]
    public void Page_BadArguments_AreAToolErrorTheModelCanRead_AndNothingIsSent(string arguments)
    {
        var handler = new CountingHandler();

        var response = Single(Serve(ConfiguredEnvironment, handler, CallPage(arguments)));

        var result = response.GetProperty("result");
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());
        StringAssert.StartsWith(result.GetProperty("content")[0].GetProperty("text").GetString(), "Not sent:");
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public void Page_NoArgumentsObjectAtAll_IsAToolError()
    {
        var handler = new CountingHandler();

        var response = Single(Serve(ConfiguredEnvironment, handler, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"page"}}"""));

        Assert.IsTrue(response.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public void ToolsCall_UnknownTool_IsInvalidParams()
    {
        var response = Single(Serve(ConfiguredEnvironment, new CountingHandler(), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}"""));

        Assert.AreEqual(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void UnknownMethod_IsMethodNotFound()
    {
        var response = Single(Serve(ConfiguredEnvironment, new CountingHandler(), """{"jsonrpc":"2.0","id":1,"method":"resources/list"}"""));

        Assert.AreEqual(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void MalformedJson_IsAParseError_AndTheServerKeepsServing()
    {
        var responses = Serve(ConfiguredEnvironment, new CountingHandler(), "{not json", """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.HasCount(2, responses);
        Assert.AreEqual(-32700, responses[0].RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.IsTrue(responses[1].RootElement.GetProperty("result").TryGetProperty("tools", out _));
    }

    [TestMethod]
    public void NoResponseEverContainsTheTopic()
    {
        var responses = Serve(
            ConfiguredEnvironment,
            new CountingHandler(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            CallPage("""{"message":"hello"}"""));

        foreach (var response in responses)
        {
            Assert.DoesNotContain(Topic, response.RootElement.GetRawText());
        }
    }

    [TestMethod]
    public void ParseArgs_Log_SetsLogDir()
    {
        Assert.AreEqual("/var/log/pager", Program.ParseArgs(["--log", "/var/log/pager"]).LogDir);
    }

    [TestMethod]
    public void ParseArgs_NoArgs_LogDirIsNull()
    {
        Assert.IsNull(Program.ParseArgs([]).LogDir);
    }

    [TestMethod]
    public void TryReadPage_TrimsTagsAndDropsABlankTitle()
    {
        using var doc = JsonDocument.Parse("""{"message":"hi","title":"  ","tags":[" a ","b"],"priority":5}""");

        Assert.IsTrue(Program.TryReadPage(doc.RootElement, out var request, out _));

        Assert.IsNull(request!.Title);
        Assert.AreEqual(5, request.Priority);
        CollectionAssert.AreEqual(new[] { "a", "b" }, request.Tags.ToArray());
    }

    [TestMethod]
    public void TryReadPage_TooLong_IsRefused()
    {
        using var longMessage = JsonDocument.Parse($$"""{"message":"{{new string('x', Program.MaxMessageLength + 1)}}"}""");
        using var longTitle = JsonDocument.Parse($$"""{"message":"x","title":"{{new string('t', Program.MaxTitleLength + 1)}}"}""");

        Assert.IsFalse(Program.TryReadPage(longMessage.RootElement, out _, out var messageProblem));
        StringAssert.Contains(messageProblem, "message");
        Assert.IsFalse(Program.TryReadPage(longTitle.RootElement, out _, out var titleProblem));
        StringAssert.Contains(titleProblem, "title");
    }
}
