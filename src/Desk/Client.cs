using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

        var request = new RequestEnvelope(Jsonrpc: "2.0", Id: RequestId, Method: method);
        writer.WriteLine(JsonSerializer.Serialize(request, SerializerOptions));
        Logger.Info($"desk: sent request: method={method}", Category);

        var replyLine = reader.ReadLine();
        if (replyLine is null)
        {
            throw new AppError("connection closed before a reply was received.", Category);
        }

        Logger.Info($"desk: received reply: {replyLine}", Category);
        return ParseResult(replyLine);
    }

    private static string ParseResult(string replyLine)
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

            return resultElement.GetString() ?? string.Empty;
        }
    }

    private sealed record RequestEnvelope(string Jsonrpc, int Id, string Method);
}
