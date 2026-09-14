using System.Text.Json;
using System.Text.Json.Serialization;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Hello;

/// <summary>
/// Minimal hand-rolled MCP server over stdio -- verified against the spec at
/// modelcontextprotocol.io/specification/2025-06-18 (basic/lifecycle, basic/transports,
/// server/tools). Handles just enough of the protocol (initialize, tools/list, tools/call) to
/// expose one tool, <see cref="SayHelloToolName"/>. No SDK: per-repo direction is to hand-roll
/// JSON-RPC over this project's own transports rather than depend on the official MCP SDK's
/// hosting/transport assumptions.
/// </summary>
public sealed record CliArguments(string? LogDir = null);

public static class Program
{
    private const string SupportedProtocolVersion = "2025-06-18";
    private const string SayHelloToolName = "say_hello";
    private const string RequestCategory = "mcp";

    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int InvalidParamsCode = -32602;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Dictionary<string, object> SayHelloInputSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
        ["additionalProperties"] = false,
    };

    public static int Main(string[] args) => Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (same convention as Service's Program.cs).
    ///
    /// Installs a silent <see cref="DiagnosticsLog"/> sink before anything else can log --
    /// specifically before <see cref="Context.Start"/>'s internal <see cref="Settings.Load"/> call,
    /// which emits its own <c>Logger.Diagnostic</c>/<c>Warning</c>/<c>Info</c> calls as part of
    /// normal operation. Those would otherwise reach <c>Logger</c>'s lazily-created default
    /// <see cref="ConsoleLog"/> sink and print straight to stdout, corrupting the MCP protocol
    /// stream (see the class doc comment: stdout must carry only valid MCP messages).
    /// <see cref="DiagnosticsLog"/>'s own <c>Log()</c> is a pure no-op for presentation (records to
    /// its pending buffer only, never prints), while <c>Print()</c> -- used for the actual protocol
    /// response lines in <see cref="WriteResult"/>/<see cref="WriteError"/> -- is defined on that
    /// same base class and writes unconditionally regardless of which sink is active, so it still
    /// works correctly here. No <see cref="IConsole"/> lifecycle concerns of its own (unlike
    /// Service, this is a plain stdio server, never a hidden-subsystem Windows app), hence
    /// <see cref="VoidConsole"/>; no CLI-driven debug override, hence the literal <c>false</c> --
    /// <c>settings.debug</c> alone still drives it if set. <c>--log</c> is the one CLI flag Hello
    /// does parse (see <see cref="ParseArgs"/>), since a persisted log file is often the only way to
    /// debug a stdio server that can never print to its own console.
    /// </summary>
    internal static int Start(string[]? argv = null, string? settingsPath = null)
    {
        Logger.SetLogger(new DiagnosticsLog());

        var arguments = ParseArgs(argv ?? []);

        return Context.Start(new VoidConsole(), "hello", settingsPath, debugOverride: false, arguments.LogDir, () => Run());
    }

    /// <summary>
    /// Hand-rolled, mirrors Service's own ParseArgs -- <c>--log &lt;dir&gt;</c> is the only flag
    /// Hello needs today. Errors go through <see cref="Logger"/> same as Service's own error
    /// handling, even though nothing reaches stdout at this point (see <see cref="Start"/>'s own
    /// remarks) -- consistent behavior (log-and-exit-nonzero) matters more here than a message
    /// anyone will actually see.
    /// </summary>
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
                    Logger.Error("hello: error: --log requires a directory argument");
                    Environment.Exit(2);
                }

                logDir = argv[++i];
            }
            else
            {
                Logger.Error($"hello: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        return new CliArguments(LogDir: logDir);
    }

    /// <summary>
    /// Reads newline-delimited JSON-RPC messages from <paramref name="input"/> (defaults to
    /// Console.In) until it hits EOF (the client closing stdin, per the stdio transport's shutdown
    /// sequence), dispatching each to a handler and writing at most one response line per request
    /// (none for notifications).
    /// </summary>
    public static int Run(TextReader? input = null)
    {
        // The stdio transport requires bare \n framing, not the platform default (\r\n on
        // Windows) -- Logger.Print()'s underlying Console.WriteLine() honors Console.Out.NewLine,
        // so setting this once up front is enough; no raw Console.Write call needed here.
        Console.Out.NewLine = "\n";

        var reader = input ?? Console.In;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                HandleLine(line);
            }
        }

        return 0;
    }

    /// <summary>
    /// Logs the raw incoming line before attempting to parse it -- deliberately unconditional on
    /// parse success, so a malformed request is still visible in a FileLog file (see
    /// <see cref="Context.Create"/>) even though it never reaches a handler. Never reaches stdout
    /// regardless of level/category filtering: Hello never installs a <see cref="ConsoleLog"/> (see
    /// <see cref="Start"/>'s own remarks), and <see cref="Logger.Info"/> -- unlike
    /// <see cref="Logger.Print"/> -- goes through each sink's normal <c>Log()</c> path, which the
    /// silent placeholder <see cref="DiagnosticsLog"/> sink never presents.
    /// </summary>
    private static void HandleLine(string line)
    {
        Logger.Info($"hello: received request: {line}", RequestCategory);

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
                // A notification (e.g. notifications/initialized) -- consumed, no response ever
                // sent, regardless of which method it names.
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
                        HandleToolsCall(idElement, paramsElement);
                        break;
                    default:
                        WriteError(idElement, MethodNotFoundCode, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A single malformed-but-parseable request shouldn't take the whole server down --
                // stdin is a system boundary (arbitrary client input), so this is exactly the kind
                // of edge the repo's error-handling guidance says is worth guarding.
                WriteError(idElement, -32603, $"Internal error: {error.Message}");
            }
        }
    }

    private static void HandleInitialize(JsonElement id)
    {
        var result = new InitializeResult(
            ProtocolVersion: SupportedProtocolVersion,
            Capabilities: new InitializeCapabilities(Tools: new ToolsCapability(ListChanged: false)),
            ServerInfo: new ServerInfo(Name: "hello", Version: "0.1.0"));

        WriteResult(id, result);
    }

    private static void HandleToolsList(JsonElement id)
    {
        var tool = new ToolDefinition(
            Name: SayHelloToolName,
            Description: "Prints a friendly greeting.",
            InputSchema: SayHelloInputSchema);

        WriteResult(id, new ToolsListResult(Tools: [tool]));
    }

    private static void HandleToolsCall(JsonElement id, JsonElement paramsElement)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object ||
            !paramsElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            WriteError(id, InvalidParamsCode, "Invalid params: 'name' is required.");
            return;
        }

        var name = nameElement.GetString() ?? string.Empty;
        if (name != SayHelloToolName)
        {
            WriteError(id, InvalidParamsCode, $"Unknown tool: {name}");
            return;
        }

        var result = new ToolCallResult(
            Content: [new TextContent(Type: "text", Text: "Hi from MCP")],
            IsError: false);

        WriteResult(id, result);
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
