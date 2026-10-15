using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Pager;

/// <summary>
/// Standalone MCP server over stdio that sends push notifications through ntfy -- one tool,
/// <see cref="PageToolName"/>. Hand-rolled JSON-RPC like <c>src/Hello</c> (verified against the spec at
/// modelcontextprotocol.io/specification/2025-06-18: basic/lifecycle, basic/transports, server/tools), no
/// SDK. It stands alone on purpose (docs/ARCHITECTURE.md, invariants 1 and 3): it references only <c>Base</c>,
/// runs straight from <c>.mcp.json</c> (<c>"command": "pager"</c>) with <c>NTFY_TOPIC</c> set by hand, and
/// when Service launches it the topic arrives in that same variable. Not configured is an answer to the
/// <c>page</c> call, not a reason to fail to start.
/// </summary>
public sealed record CliArguments(string? LogDir = null);

public static class Program
{
    private const string SupportedProtocolVersion = "2025-06-18";
    internal const string PageToolName = "page";
    private const string RequestCategory = "mcp";

    internal const int MaxMessageLength = 4096;
    internal const int MaxTitleLength = 250;
    internal const int MaxTagLength = 50;
    internal const int MaxTags = 10;

    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int InvalidParamsCode = -32602;
    private const int InternalErrorCode = -32603;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Dictionary<string, object> PageInputSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "The notification text.",
                ["maxLength"] = MaxMessageLength,
            },
            ["title"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional short title shown above the message.",
                ["maxLength"] = MaxTitleLength,
            },
            ["priority"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Optional urgency, 1 (min) to 5 (max); 3 is the default. Use 4-5 only for something that needs attention now.",
                ["minimum"] = 1,
                ["maximum"] = 5,
            },
            ["tags"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Optional ntfy tags (a known emoji short code such as 'white_check_mark' or 'warning' shows as an emoji).",
                ["items"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = MaxTagLength },
                ["maxItems"] = MaxTags,
            },
        },
        ["required"] = new[] { "message" },
        ["additionalProperties"] = false,
    };

    // See DevRedirect's own remarks -- must run before anything else Main does. No-op (returns null)
    // unless a ".dev" junction actually exists alongside this exe's own install folder.
    public static int Main(string[] args) => DevRedirect.TryHandoff("Pager.exe", args) ?? Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. The seams (<paramref name="settingsPath"/>,
    /// <paramref name="input"/>/<paramref name="output"/> in place of the process-wide console,
    /// <paramref name="getEnvironmentVariable"/>, <paramref name="handler"/> in place of the network) are all
    /// null in the real run. Installs a silent <see cref="DiagnosticsLog"/> before anything can log, exactly
    /// as <c>Hello</c> does: stdout carries only MCP messages, and its own <c>Print</c> still writes them.
    /// </summary>
    internal static int Start(
        string[]? argv = null,
        string? settingsPath = null,
        TextReader? input = null,
        TextWriter? output = null,
        Func<string, string?>? getEnvironmentVariable = null,
        HttpMessageHandler? handler = null)
    {
        Logger.SetLogger(new DiagnosticsLog(printWriter: output));

        var arguments = ParseArgs(argv ?? []);

        // Exit once Service (the launcher, per DESK_PARENT_PID) is gone -- no-op when run any other way,
        // including every unit test. See ParentWatchdog / issue #45.
        ParentWatchdog.Start();

        int RunServer() => Run(input, PagerConfig.FromEnvironment(getEnvironmentVariable), handler);

        return Context.Start(new VoidConsole(), "pager", settingsPath, debugOverride: false, arguments.LogDir, RunServer);
    }

    /// <summary>Hand-rolled like Hello's: <c>--log &lt;dir&gt;</c> is the only flag.</summary>
    internal static CliArguments ParseArgs(string[] argv)
    {
        string? logDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--log")
            {
                if (i + 1 >= argv.Length)
                {
                    Logger.Error("pager: error: --log requires a directory argument");
                    Environment.Exit(2);
                }

                logDir = argv[++i];
            }
            else
            {
                Logger.Error($"pager: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        return new CliArguments(LogDir: logDir);
    }

    /// <summary>
    /// Reads newline-delimited JSON-RPC messages until EOF (the client closing stdin, per the stdio
    /// transport's shutdown sequence), writing at most one response line per request.
    /// </summary>
    internal static int Run(TextReader? input = null, PagerConfig? config = null, HttpMessageHandler? handler = null)
    {
        // The stdio transport requires bare \n framing, not the platform default.
        Console.Out.NewLine = "\n";

        var resolved = config ?? PagerConfig.FromEnvironment();
        if (resolved.IsConfigured)
        {
            Logger.Info($"pager: started; publishing to '{resolved.Server.Host}'.", RequestCategory);
        }
        else
        {
            Logger.Warning($"pager: started, but not configured: {resolved.Problem}", RequestCategory);
        }

        using var notifier = new Notifier(resolved, handler);

        var reader = input ?? Console.In;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                HandleLine(line, notifier);
            }
        }

        return 0;
    }

    /// <summary>
    /// Logs the raw incoming line before parsing it, so a malformed request still shows in a FileLog file.
    /// Never reaches stdout (see <see cref="Start"/>). The notification text is not a secret; the topic never
    /// appears in a request.
    /// </summary>
    private static void HandleLine(string line, Notifier notifier)
    {
        Logger.Info($"pager: received request: {line}", RequestCategory);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            WriteError(id: null, ParseErrorCode, "Parse error");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hasId = root.TryGetProperty("id", out var idElement);

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                if (hasId)
                {
                    WriteError(idElement, InvalidRequestCode, "Invalid request: 'method' is required.");
                }

                return;
            }

            if (!hasId)
            {
                // A notification (e.g. notifications/initialized) -- consumed, never answered.
                return;
            }

            var method = methodElement.GetString() ?? string.Empty;
            var paramsElement = root.TryGetProperty("params", out var p) ? p : default;

            try
            {
                switch (method)
                {
                    case "initialize":
                        HandleInitialize(idElement);
                        break;
                    case "tools/list":
                        HandleToolsList(idElement);
                        break;
                    case "tools/call":
                        HandleToolsCall(idElement, paramsElement, notifier);
                        break;
                    default:
                        WriteError(idElement, MethodNotFoundCode, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // stdin is a system boundary (arbitrary client input): one bad request must not take the server down.
                WriteError(idElement, InternalErrorCode, $"Internal error: {error.Message}");
            }
        }
    }

    private static void HandleInitialize(JsonElement id)
    {
        var result = new InitializeResult(
            ProtocolVersion: SupportedProtocolVersion,
            Capabilities: new InitializeCapabilities(Tools: new ToolsCapability(ListChanged: false)),
            ServerInfo: new ServerInfo(Name: "pager", Version: "0.1.0"));

        WriteResult(id, result);
    }

    private static void HandleToolsList(JsonElement id)
    {
        var pageTool = new ToolDefinition(
            Name: PageToolName,
            Description: "Sends a push notification to the user's phone or desktop through ntfy. Use it to tell them "
                + "something they asked to be told about (a long job finished, something needs their decision). "
                + "Do not use it for routine progress updates or to repeat what you are already saying in the conversation.",
            InputSchema: PageInputSchema);

        WriteResult(id, new ToolsListResult(Tools: [pageTool]));
    }

    private static void HandleToolsCall(JsonElement id, JsonElement paramsElement, Notifier notifier)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object ||
            !paramsElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            WriteError(id, InvalidParamsCode, "Invalid params: 'name' is required.");
            return;
        }

        var name = nameElement.GetString() ?? string.Empty;
        if (name != PageToolName)
        {
            WriteError(id, InvalidParamsCode, $"Unknown tool: {name}");
            return;
        }

        var arguments = paramsElement.TryGetProperty("arguments", out var a) ? a : default;
        if (!TryReadPage(arguments, out var request, out var problem))
        {
            // Bad arguments are a tool failure the model can read and correct, not a protocol error.
            WriteResult(id, ToolText($"Not sent: {problem}", isError: true));
            return;
        }

        var result = notifier.Send(request!);
        WriteResult(id, ToolText(result.Text, isError: !result.Sent));
    }

    /// <summary>Validates a <c>page</c> call's arguments. On failure <paramref name="problem"/> says what to fix.</summary>
    internal static bool TryReadPage(JsonElement arguments, out PageRequest? request, out string? problem)
    {
        request = null;
        problem = null;

        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(messageElement.GetString()))
        {
            problem = "'message' (a non-empty string) is required.";
            return false;
        }

        var message = messageElement.GetString()!;
        if (message.Length > MaxMessageLength)
        {
            problem = $"'message' is longer than {MaxMessageLength} characters.";
            return false;
        }

        string? title = null;
        if (arguments.TryGetProperty("title", out var titleElement) && titleElement.ValueKind != JsonValueKind.Null)
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                problem = "'title' must be a string.";
                return false;
            }

            title = titleElement.GetString();
            if (title is not null && title.Length > MaxTitleLength)
            {
                problem = $"'title' is longer than {MaxTitleLength} characters.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = null;
            }
        }

        int? priority = null;
        if (arguments.TryGetProperty("priority", out var priorityElement) && priorityElement.ValueKind != JsonValueKind.Null)
        {
            if (priorityElement.ValueKind != JsonValueKind.Number || !priorityElement.TryGetInt32(out var value) || value < 1 || value > 5)
            {
                problem = "'priority' must be an integer from 1 to 5.";
                return false;
            }

            priority = value;
        }

        var tags = new List<string>();
        if (arguments.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind != JsonValueKind.Null)
        {
            if (tagsElement.ValueKind != JsonValueKind.Array)
            {
                problem = "'tags' must be an array of strings.";
                return false;
            }

            foreach (var tagElement in tagsElement.EnumerateArray())
            {
                var tag = tagElement.ValueKind == JsonValueKind.String ? tagElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength)
                {
                    problem = $"each tag must be a non-empty string of at most {MaxTagLength} characters.";
                    return false;
                }

                tags.Add(tag.Trim());
            }

            if (tags.Count > MaxTags)
            {
                problem = $"at most {MaxTags} tags are allowed.";
                return false;
            }
        }

        request = new PageRequest(message, title, priority, tags);
        return true;
    }

    private static ToolCallResult ToolText(string text, bool isError)
    {
        return new ToolCallResult(Content: [new TextContent(Type: "text", Text: text)], IsError: isError);
    }

    private static void WriteResult(JsonElement id, object result)
    {
        var envelope = new SuccessEnvelope(Jsonrpc: "2.0", Id: id, Result: result);
        Logger.Print(JsonSerializer.Serialize(envelope, SerializerOptions));
    }

    private static void WriteError(JsonElement? id, int code, string message)
    {
        var envelope = new ErrorEnvelope(Jsonrpc: "2.0", Id: id, Error: new ErrorDetail(code, message));
        Logger.Print(JsonSerializer.Serialize(envelope, SerializerOptions));
    }

    private sealed record SuccessEnvelope(string Jsonrpc, JsonElement? Id, object Result);

    private sealed record ErrorEnvelope(string Jsonrpc, JsonElement? Id, ErrorDetail Error);

    private sealed record ErrorDetail(int Code, string Message);

    private sealed record InitializeResult(string ProtocolVersion, InitializeCapabilities Capabilities, ServerInfo ServerInfo);

    private sealed record InitializeCapabilities(ToolsCapability Tools);

    private sealed record ToolsCapability(bool ListChanged);

    private sealed record ServerInfo(string Name, string Version);

    private sealed record ToolsListResult(List<ToolDefinition> Tools);

    private sealed record ToolDefinition(string Name, string Description, object InputSchema);

    private sealed record ToolCallResult(List<TextContent> Content, bool IsError);

    private sealed record TextContent(string Type, string Text);
}
