using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Client for <c>src/Service</c>'s <c>Host</c>: connects to its loopback TCP listener, sends one
/// newline-delimited line, and returns the line echoed back -- see docs/PROTOCOL.md for the wire
/// format <c>Host</c> implements. Constructor takes an optional <paramref name="port"/> override
/// (same pattern as <c>Host</c>'s own constructor) so a test can point this at an ephemeral port a
/// test-local <c>Host</c> is actually listening on, instead of the real
/// <see cref="ISettingsProvider.Port"/>.
/// </summary>
internal sealed class Client
{
    private const string Category = "desk";

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
    /// Connects, sends <paramref name="message"/> as a single line, and returns the line echoed
    /// back -- fails fast with an <see cref="AppError"/> (no retry) both when the connection itself
    /// fails and when the connection closes without ever sending a reply line.
    /// </summary>
    public string SendEcho(string message)
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

        writer.WriteLine(message);
        Logger.Info($"desk: sent echo request: {message}", Category);

        var reply = reader.ReadLine();
        if (reply is null)
        {
            throw new AppError("connection closed before a reply was received.", Category);
        }

        Logger.Info($"desk: received reply: {reply}", Category);
        return reply;
    }
}
