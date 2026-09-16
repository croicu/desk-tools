using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Client for <c>src/Service</c>'s <c>Host</c>: connects to its loopback TCP listener, sends one
/// JSON-RPC 2.0 request line, and returns the result -- see docs/PROTOCOL.md for the wire format
/// <c>Host</c> implements (see also [issue #31](https://github.com/croicu/desk-tools/issues/31),
/// which moved this off the earlier plain-text echo protocol). Constructor takes an optional
/// <paramref name="port"/> override (same pattern as <c>Host</c>'s own constructor) so a test can
/// point this at an ephemeral port a test-local <c>Host</c> is actually listening on, instead of the
/// real <see cref="ISettingsProvider.Port"/>.
/// </summary>
internal sealed class Client
{
    private const string Category = "desk";

    // A fixed id: exactly one request per connection, no concurrent in-flight requests on the same
    // Client to distinguish, so there's nothing a varying id would buy here -- same reasoning
    // Hello's own dispatch has no need to mint ids (it only ever echoes back whatever a request
    // supplied).
    private const int RequestId = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Omits "params" entirely for a no-params request (ping/shutdown) rather than serializing
        // a literal "params": null -- Host's own dispatch treats both the same way (an absent
        // "params" is just an empty JsonElement), but the wire text stays cleaner.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // See Host.WriteEncoding's own remarks -- same reasoning, same fix, on this exchange's other
    // end: Encoding.UTF8's BOM preamble would otherwise get written on the first Flush() even if
    // Host never actually wrote a reply.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ISettingsProvider _settings;
    private readonly int? _portOverride;

    public Client(ISettingsProvider settings, int? port = null)
    {
        _settings = settings;
        _portOverride = port;
    }

    internal int Port => _portOverride ?? _settings.Port;

    /// <summary>
    /// Connects, sends <paramref name="method"/> as a JSON-RPC request with no params, and returns
    /// its string result -- fails fast with an <see cref="AppError"/> (no retry) when the connection
    /// itself fails, when the connection closes without ever sending a reply line, when the reply
    /// isn't valid JSON-RPC, or when it's a JSON-RPC error response (the error's own message is
    /// folded into the <see cref="AppError"/>).
    /// </summary>
    public string Send(string method)
    {
        var replyLine = SendRequest(method, paramsObject: null);
        var result = ParseResultElement(replyLine);
        return result.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Sends an <c>mcp</c> request for the registry tool named <paramref name="name"/>, with this
    /// process's own PID as the target for Service's <c>DuplicateHandle</c>-based handoff (see
    /// docs/PROTOCOL.md's <c>mcp</c> method, [issue #32](https://github.com/croicu/desk-tools/issues/32))
    /// -- returns the two duplicated handle values from the result, ready for
    /// <see cref="McpProxy.Run"/> to open. Same fail-fast <see cref="AppError"/> conventions as
    /// <see cref="Send"/>.
    /// </summary>
    public (long Stdin, long Stdout) LaunchMcpTool(string name)
    {
        var replyLine = SendRequest("mcp", new McpParams(name, Environment.ProcessId));
        var result = ParseResultElement(replyLine);

        var stdin = long.Parse(result.GetProperty("stdin").GetString()!);
        var stdout = long.Parse(result.GetProperty("stdout").GetString()!);
        return (stdin, stdout);
    }

    private string SendRequest(string method, object? paramsObject)
    {
        Logger.Info($"desk: connecting to 127.0.0.1:{Port}.", Category);

        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, Port);
        }
        catch (SocketException error)
        {
            throw new AppError($"could not connect to the service on port {Port}: {error.Message}", Category);
        }

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, WriteEncoding) { NewLine = "\n", AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var request = new RequestEnvelope(Jsonrpc: "2.0", Id: RequestId, Method: method, Params: paramsObject);
        writer.WriteLine(JsonSerializer.Serialize(request, SerializerOptions));
        Logger.Info($"desk: sent request: method={method}", Category);

        var replyLine = reader.ReadLine();
        if (replyLine is null)
        {
            throw new AppError("connection closed before a reply was received.", Category);
        }

        Logger.Info($"desk: received reply: {replyLine}", Category);
        return replyLine;
    }

    /// <summary>
    /// Parses a reply line into its <c>result</c> element -- throws <see cref="AppError"/> on
    /// malformed JSON, a JSON-RPC error response, or a reply with neither <c>result</c> nor
    /// <c>error</c>. <see cref="JsonElement.Clone"/> because the backing <see cref="JsonDocument"/>
    /// is disposed before this returns; without cloning, the returned element would become invalid
    /// the moment the caller tries to read it.
    /// </summary>
    private static JsonElement ParseResultElement(string replyLine)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(replyLine);
        }
        catch (JsonException error)
        {
            throw new AppError($"reply was not valid JSON-RPC: {error.Message}", Category);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                var code = errorElement.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : 0;
                var message = errorElement.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "(no message)";
                throw new AppError($"request failed: {message} (code {code})", Category);
            }

            if (!root.TryGetProperty("result", out var resultElement))
            {
                throw new AppError($"reply had neither 'result' nor 'error': {replyLine}", Category);
            }

            return resultElement.Clone();
        }
    }

    private sealed record RequestEnvelope(string Jsonrpc, int Id, string Method, object? Params = null);

    private sealed record McpParams(string Name, int ProcessId);
}
