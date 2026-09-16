using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Resident-process skeleton (see tasks/resident-process-skeleton.md): stays alive accepting TCP
/// connections, handling one JSON-RPC 2.0 request per connection (see <see cref="HandleConnection"/>
/// and docs/PROTOCOL.md), then exits once no connection has been in flight for
/// <see cref="ISettingsProvider.IdleTimeout"/> -- or once a client sends a <see cref="ShutdownMethod"/>
/// request instead of <see cref="PingMethod"/>, triggering the exact same shutdown path early (see
/// issue #22: deliberately shares <see cref="_shutdownSignal"/> with the idle-timeout path rather
/// than growing a second one, since there's nothing else a graceful shutdown needs to do yet).
/// Accepting a connection is the interim activity signal ("Activity tracking (interim)" in the task
/// doc): the real spec wants the timer reset on completed request, not connection open. The port to
/// listen on comes from <see cref="ISettingsProvider.Port"/> (shared settings.json, so a separate
/// client process -- <c>src/Desk</c> -- can learn the same value independently). See
/// [issue #31](https://github.com/croicu/desk-tools/issues/31) for the switch away from this class's
/// earlier plain-text echo protocol -- mirrors <c>src/Hello/Program.cs</c>'s own hand-rolled
/// JSON-RPC dispatch style (same error codes, same envelope shapes), just over a TCP connection
/// instead of stdio, and one request per connection instead of a persistent read loop.
/// </summary>
internal sealed class Host
{
    /// <summary>No params, result <c>"pong"</c> -- a liveness/reachability check.</summary>
    internal const string PingMethod = "ping";

    /// <summary>
    /// No params, result <c>"ok"</c> -- asks <see cref="Host"/> to shut down gracefully (see
    /// <see cref="HandleConnection"/>). Still replies first (same as <see cref="PingMethod"/>), so
    /// the client gets a definitive acknowledgment before the listener actually stops. Duplicated
    /// (not shared via a project reference) as a literal in <c>src/Desk/Program.cs</c> and
    /// <c>scripts/shutdown_service.py</c>, which deliberately have no <c>ProjectReference</c>/import
    /// on <c>Service.csproj</c> -- keep the literals in sync by hand.
    /// </summary>
    internal const string ShutdownMethod = "shutdown";

    private const string Category = "host";

    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int InternalErrorCode = -32603;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Not Encoding.UTF8 -- that instance's GetPreamble() is a 3-byte BOM, and StreamWriter writes
    // it unconditionally on its first Flush() even when zero characters were ever written (e.g. a
    // client that sends no line at all), corrupting the wire with bytes no client asked for. A
    // StreamReader using Encoding.UTF8 silently strips a leading BOM on read (its default
    // detectEncodingFromByteOrderMarks), which is exactly why this only ever showed up in a raw
    // byte-level test, never in one going through StreamReader.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(2);

    private readonly ISettingsProvider _settings;
    private readonly TcpListener _listener;
    private readonly ManualResetEventSlim _shutdownSignal = new(initialState: false);
    private readonly object _activityLock = new();

    private DateTimeOffset _lastActivity;
    private int _inFlightCount;
    private Timer? _idleTimer;
    private volatile bool _accepting;
    private Thread? _acceptThread;

    public Host(ISettingsProvider settings, int? port = null)
    {
        _settings = settings;
        _listener = new TcpListener(IPAddress.Loopback, port ?? settings.Port);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>Bound listening port -- only valid after <see cref="Start"/> has run.</summary>
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Binds the listener and starts the accept loop and idle-check timer. Split out from
    /// <see cref="WaitForIdleShutdown"/> (rather than one combined blocking call) so a test can read
    /// <see cref="Port"/> and connect against it before blocking on shutdown -- <see cref="Run"/>
    /// below, which is what Program.cs actually calls, is just the two in sequence.
    /// </summary>
    internal void Start()
    {
        _listener.Start();
        Logger.Info($"host: listening on port {Port}.", Category);

        _accepting = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();

        _idleTimer = new Timer(CheckIdle, state: null, IdleCheckInterval, IdleCheckInterval);
    }

    /// <summary>
    /// Blocks the calling thread until <see cref="_shutdownSignal"/> is set -- by
    /// <see cref="CheckIdle"/> once idle long enough, or by <see cref="HandleConnection"/> on a
    /// <see cref="ShutdownMethod"/> request -- then stops the listener and joins the accept thread.
    /// By the time this returns, the listener is closed and no thread is left running, regardless of
    /// which of the two triggered it.
    /// </summary>
    internal void WaitForIdleShutdown()
    {
        _shutdownSignal.Wait();

        _accepting = false;
        _idleTimer?.Dispose();
        _listener.Stop();
        _acceptThread?.Join();

        Logger.Info("host: shutting down.", Category);
    }

    public void Run()
    {
        Start();
        WaitForIdleShutdown();
    }

    private void AcceptLoop()
    {
        while (_accepting)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                // Listener was stopped from WaitForIdleShutdown -- exit the loop cleanly.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(HandleConnection, client, preferLocal: false);
        }
    }

    /// <summary>
    /// Named handler (not a lambda) per the task doc's Design decisions -- accept, dispatch, close,
    /// record activity, and track in-flight count around the handling so <see cref="CheckIdle"/>
    /// never fires mid-connection. Reads at most one line (one JSON-RPC request, newline-delimited)
    /// and writes at most one JSON-RPC response line back, then closes; a client that sends nothing
    /// (EOF with no line) gets no reply, just a closed connection -- same as a JSON-RPC notification
    /// (a request with no <c>id</c>) would. Signals shutdown only after this connection's own reply
    /// has been written and its resources disposed -- the client always gets its acknowledgment, and
    /// the listener never stops mid-write of an unrelated in-flight reply.
    /// </summary>
    private void HandleConnection(TcpClient client)
    {
        Interlocked.Increment(ref _inFlightCount);
        try
        {
            RecordActivity();

            var shutdownRequested = false;
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, WriteEncoding) { NewLine = "\n", AutoFlush = true })
            {
                var line = reader.ReadLine();
                if (line is not null)
                {
                    shutdownRequested = HandleLine(line, writer);
                }
            }

            if (shutdownRequested)
            {
                Logger.Info("host: shutdown requested by a client; signaling shutdown.", Category);
                _shutdownSignal.Set();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightCount);
        }
    }

    /// <summary>
    /// Parses and dispatches one JSON-RPC request line, mirroring
    /// <c>src/Hello/Program.cs</c>'s own <c>HandleLine</c> (same error codes/envelope shapes). Returns
    /// whether <see cref="HandleConnection"/> should now signal shutdown -- only
    /// <see cref="ShutdownMethod"/> returns true, and only after its own response has already been
    /// written, so the client always gets a normal result for the shutdown request itself before the
    /// listener actually stops.
    /// </summary>
    private static bool HandleLine(string line, StreamWriter writer)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            WriteError(writer, id: null, ParseErrorCode, "Parse error");
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hasId = root.TryGetProperty("id", out var idElement);

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                if (hasId)
                {
                    WriteError(writer, idElement, InvalidRequestCode, "Invalid request: 'method' is required.");
                }

                return false;
            }

            if (!hasId)
            {
                // A notification -- consumed, no response ever sent, regardless of which method it
                // names (same convention Hello's own dispatch uses).
                return false;
            }

            var method = methodElement.GetString() ?? string.Empty;

            try
            {
                switch (method)
                {
                    case PingMethod:
                        WriteResult(writer, idElement, "pong");
                        return false;
                    case ShutdownMethod:
                        WriteResult(writer, idElement, "ok");
                        return true;
                    default:
                        WriteError(writer, idElement, MethodNotFoundCode, $"Method not found: {method}");
                        return false;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A single malformed-but-parseable request shouldn't take the whole listener down --
                // the connection is a system boundary (arbitrary client input), same reasoning as
                // Hello's own catch-all.
                WriteError(writer, idElement, InternalErrorCode, $"Internal error: {error.Message}");
                return false;
            }
        }
    }

    private static void WriteResult(StreamWriter writer, JsonElement id, object result)
    {
        var envelope = new SuccessEnvelope(Jsonrpc: "2.0", Id: id, Result: result);
        writer.WriteLine(JsonSerializer.Serialize(envelope, SerializerOptions));
    }

    private static void WriteError(StreamWriter writer, JsonElement? id, int code, string message)
    {
        var envelope = new ErrorEnvelope(Jsonrpc: "2.0", Id: id, Error: new ErrorDetail(code, message));
        writer.WriteLine(JsonSerializer.Serialize(envelope, SerializerOptions));
    }

    private void RecordActivity()
    {
        lock (_activityLock)
        {
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    private void CheckIdle(object? state)
    {
        if (Volatile.Read(ref _inFlightCount) > 0)
        {
            return;
        }

        TimeSpan idleFor;
        lock (_activityLock)
        {
            idleFor = DateTimeOffset.UtcNow - _lastActivity;
        }

        if (idleFor.TotalSeconds >= _settings.IdleTimeout)
        {
            _shutdownSignal.Set();
        }
    }

    private sealed record SuccessEnvelope(string Jsonrpc, JsonElement Id, object Result);

    private sealed record ErrorEnvelope(string Jsonrpc, JsonElement? Id, ErrorDetail Error);

    private sealed record ErrorDetail(int Code, string Message);
}
