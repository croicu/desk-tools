using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Secrets;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Resident-process skeleton (see tasks/resident-process-skeleton.md): stays alive accepting TCP
/// connections, handling one JSON-RPC 2.0 request per connection (see <see cref="HandleConnection"/>
/// and docs/PROTOCOL.md), then exits once no connection has been in flight for
/// <see cref="ISettingsProvider.IdleTimeout"/> *and* no handed-off MCP tool is still running (issue
/// #45; see <see cref="CheckIdle"/>) -- or once a client sends a <see cref="ShutdownMethod"/>
/// request instead of <see cref="PingMethod"/>, triggering the exact same shutdown path early (see
/// issue #22: deliberately shares <see cref="_shutdownSignal"/> with the idle-timeout path rather
/// than growing a second one, since there's nothing else a graceful shutdown needs to do yet).
/// Accepting a connection is the interim activity signal ("Activity tracking (interim)" in the task
/// doc): the real spec wants the timer reset on completed request, not connection open. The port to
/// listen on comes from <see cref="ISettingsProvider.Port"/> (shared settings.json, so a separate
/// client process -- <c>src/Desk</c> -- can learn the same value independently). See
/// [issue #31](https://github.com/croicu/desk-tools/issues/31) for the switch away from this class's
/// earlier plain-text echo protocol -- mirrors <c>tests/MCP/Hello/Program.cs</c>'s own hand-rolled
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
    /// (not shared via a project reference) as a literal in <c>src/Desk/Program.cs</c>, which
    /// deliberately has no <c>ProjectReference</c> on <c>Service.csproj</c> -- keep the literals in
    /// sync by hand.
    /// </summary>
    internal const string ShutdownMethod = "shutdown";

    /// <summary>
    /// No params, result <c>"ok"</c> -- forgets the KeePass master password, the one held in memory and the one remembered
    /// on disk (issue #84), so the next secret request asks again. Goes through Service because only the elevated Service
    /// can touch the admin-only file. An error if this Service has no secret broker (non-Windows).
    /// </summary>
    internal const string ForgetPasswordMethod = "forget-password";

    /// <summary>
    /// Params <c>{"name": "&lt;registry name&gt;", "processId": &lt;caller's own PID&gt;}</c>, plus an
    /// optional <c>"registryDir"</c> (a caller-chosen registry searched first, honored only when
    /// <see cref="ISettingsProvider.AllowCallerRegistry"/> is on -- otherwise the request fails; issue
    /// #69). Launches the named <c>mcp-registry/</c> tool (see <see cref="McpToolLauncher"/>) and duplicates its
    /// stdin/stdout pipe handles directly into the caller's own process (see
    /// <see cref="HandleDuplicator"/>, issue #32) -- Service steps out of the exchange entirely once
    /// this returns, rather than relaying bytes for the tool's whole lifetime. Result
    /// <c>{"stdin": "&lt;decimal handle value&gt;", "stdout": "&lt;decimal handle value&gt;"}</c> -- text,
    /// not a JSON number, since a Win32 <c>HANDLE</c> is a pointer (8 bytes on x64) and a JSON
    /// number can't reliably round-trip that precision. Trusts the caller-supplied <c>processId</c>
    /// as-is -- no verification against the real TCP connection's owning process (a known,
    /// deliberate simplification; the loopback listener is local-machine-only exposure either way).
    /// </summary>
    internal const string McpMethod = "mcp";

    private const string Category = "host";

    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int InvalidParamsCode = -32602;
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

    private readonly string? _mcpRegistryBaseDirectory;
    private readonly ISecretBroker? _secrets;

    // Watch-only handles (no stdio) to tools already handed off to a client -- see TrackChild.
    private readonly List<Process> _children = [];
    private readonly object _childrenLock = new();
    private bool _waitingOnChildren; // Only touched from the single idle-timer callback.

    private DateTimeOffset _lastActivity;
    private int _inFlightCount;
    private Timer? _idleTimer;
    private volatile bool _accepting;
    private Thread? _acceptThread;

    /// <summary>
    /// <paramref name="mcpRegistryBaseDirectory"/> is a testing seam for <see cref="McpMethod"/>
    /// (defaults to <see cref="McpToolLauncher.Launch"/>'s own <see cref="AppContext.BaseDirectory"/>
    /// default, Service's real behavior) -- same pattern as <paramref name="port"/>, lets a test
    /// point this at a throwaway registry fixture instead of the real, build-generated one.
    /// </summary>
    public Host(ISettingsProvider settings, int? port = null, string? mcpRegistryBaseDirectory = null, ISecretBroker? secrets = null)
    {
        _secrets = secrets;
        _settings = settings;
        _listener = new TcpListener(IPAddress.Loopback, port ?? settings.Port);
        _mcpRegistryBaseDirectory = mcpRegistryBaseDirectory;
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

        // Release the watch handles only -- the tools themselves keep running until their own
        // parent watchdog (see ParentWatchdog) notices this process is gone.
        lock (_childrenLock)
        {
            foreach (var child in _children)
            {
                child.Dispose();
            }

            _children.Clear();
        }

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
        // Nothing may escape this callback: it runs on a thread-pool thread, where an unhandled exception ends
        // the whole process (and every tool Service launched with it) -- see ServeConnection.
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                ServeConnection(stream);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // GetStream() or the dispose of an already-broken connection.
            Logger.Warning($"host: a connection could not be closed cleanly: {error.Message}", Category);
        }
    }

    /// <summary>
    /// Serves one request on an already-open connection. <b>Never lets a failure on the connection escape</b>
    /// (issue #78): a client that has gone away before its reply is written is routine -- an MCP client
    /// kills its Desk proxy while the launch is still waiting for the master-password dialog, say -- and
    /// the failed write would otherwise be an unhandled exception on a thread-pool thread, which kills the
    /// whole Service and every tool it launched. A client that vanished is a Warning; anything else
    /// unexpected is an Error; either way the connection is simply dropped. A <c>shutdown</c> whose
    /// acknowledgment could not be delivered is still honored. Internal so a test can hand it a stream that
    /// fails the way a dead connection does.
    /// </summary>
    internal void ServeConnection(Stream stream)
    {
        Interlocked.Increment(ref _inFlightCount);
        try
        {
            RecordActivity();

            var shutdownRequested = false;
            try
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                using (var writer = new StreamWriter(stream, WriteEncoding, bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true })
                {
                    var line = reader.ReadLine();
                    if (line is not null)
                    {
                        shutdownRequested = HandleLine(line, writer);
                    }
                }
            }
            catch (IOException error)
            {
                Logger.Warning($"host: the client went away before its request was served: {error.Message}", Category);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Logger.Error($"host: unexpected failure serving a connection: {error}", Category);
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
    /// <c>tests/MCP/Hello/Program.cs</c>'s own <c>HandleLine</c> (same error codes/envelope shapes). Returns
    /// whether <see cref="HandleConnection"/> should now signal shutdown -- only
    /// <see cref="ShutdownMethod"/> returns true, and only after its own response has already been
    /// written, so the client always gets a normal result for the shutdown request itself before the
    /// listener actually stops.
    /// </summary>
    private bool HandleLine(string line, StreamWriter writer)
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
            var paramsElement = root.TryGetProperty("params", out var p) ? p : default;

            try
            {
                switch (method)
                {
                    case PingMethod:
                        WriteResult(writer, idElement, "pong");
                        return false;
                    case ShutdownMethod:
                        try
                        {
                            WriteResult(writer, idElement, "ok");
                        }
                        catch (IOException error)
                        {
                            // The requester went away before its acknowledgment: it still asked for the shutdown.
                            Logger.Warning($"host: could not deliver the shutdown acknowledgment ({error.Message}); shutting down anyway.", Category);
                        }

                        return true;
                    case ForgetPasswordMethod:
                        HandleForgetPassword(idElement, writer);
                        return false;
                    case McpMethod:
                        HandleMcp(idElement, paramsElement, writer);
                        return false;
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

    /// <summary>
    /// See <see cref="McpMethod"/>'s own remarks for the full contract. Validates <c>params</c>
    /// itself (missing/malformed <c>name</c>/<c>processId</c> is <see cref="InvalidParamsCode"/>,
    /// not <see cref="InternalErrorCode"/>) before doing anything with side effects; any failure from
    /// <see cref="McpToolLauncher.Launch"/> or <see cref="HandleDuplicator.Duplicate"/> (an
    /// <see cref="AppError"/>) surfaces as <see cref="InternalErrorCode"/>, same as any other
    /// unexpected runtime failure -- this repo doesn't currently distinguish "known domain failure"
    /// from "truly unexpected" in its JSON-RPC error codes (see <c>tests/MCP/Hello/Program.cs</c>'s own
    /// single catch-all), just message content.
    /// </summary>
    private void HandleMcp(JsonElement id, JsonElement paramsElement, StreamWriter writer)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object ||
            !paramsElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            WriteError(writer, id, InvalidParamsCode, "Invalid params: 'name' (string) is required.");
            return;
        }

        if (!paramsElement.TryGetProperty("processId", out var processIdElement) ||
            processIdElement.ValueKind != JsonValueKind.Number ||
            !processIdElement.TryGetInt32(out var processId))
        {
            WriteError(writer, id, InvalidParamsCode, "Invalid params: 'processId' (integer) is required.");
            return;
        }

        var name = nameElement.GetString() ?? string.Empty;

        string? callerRegistryDirectory = null;
        if (paramsElement.TryGetProperty("registryDir", out var registryDirElement))
        {
            if (registryDirElement.ValueKind != JsonValueKind.String)
            {
                WriteError(writer, id, InvalidParamsCode, "Invalid params: 'registryDir' must be a string.");
                return;
            }

            callerRegistryDirectory = registryDirElement.GetString();
            var refusal = CheckCallerRegistry(callerRegistryDirectory);
            if (refusal is not null)
            {
                Logger.Warning($"host: refusing mcp request for '{name}': {refusal}", Category);
                WriteError(writer, id, InvalidParamsCode, $"Invalid params: {refusal}");
                return;
            }
        }

        ToolProcess? tool = null;
        try
        {
            tool = McpToolLauncher.Launch(name, _mcpRegistryBaseDirectory, callerRegistryDirectory, _secrets, ToolConsole.IsVisible(name, _settings));
            var (stdin, stdout) = HandleDuplicator.Duplicate(tool, processId);
            TrackChild(name, tool.OpenWatchHandle());
            tool.DisownAfterHandoff();
            tool = null; // Ownership (and both pipe ends) already transferred -- don't tear it down below.

            Logger.Info($"host: launched mcp tool '{name}' and duplicated its stdio into process {processId}.", Category);
            WriteResult(writer, id, new McpResult(stdin.ToString(), stdout.ToString()));
        }
        finally
        {
            // Only reached if something failed after a successful Launch but before the handoff
            // completed (e.g. HandleDuplicator itself threw) -- the launched process is orphaned
            // (nobody has handles to it), so tear it down the normal graceful way rather than
            // leaking it.
            tool?.Dispose();
        }
    }

    /// <summary>
    /// Returns why a caller-supplied <c>registryDir</c> must not be used, or null if it may be. Service
    /// is elevated and reachable by any local process, so honoring one at all is opt-in
    /// (<see cref="ISettingsProvider.AllowCallerRegistry"/>, default off), and it must be a fully
    /// qualified local path -- a UNC path would make Service authenticate to an arbitrary machine.
    /// </summary>
    private string? CheckCallerRegistry(string? registryDir)
    {
        if (!_settings.AllowCallerRegistry)
        {
            return "'registryDir' is not allowed: set settings.allowCallerRegistry to true in settings.json to let callers supply a registry.";
        }

        if (string.IsNullOrWhiteSpace(registryDir) || !Path.IsPathFullyQualified(registryDir))
        {
            return "'registryDir' must be a fully qualified path.";
        }

        if (registryDir.StartsWith(@"\\", StringComparison.Ordinal) || registryDir.StartsWith("//", StringComparison.Ordinal))
        {
            return "'registryDir' must be a local path, not a network (UNC) path.";
        }

        return null;
    }

    private void HandleForgetPassword(JsonElement id, StreamWriter writer)
    {
        if (_secrets is null)
        {
            WriteError(writer, id, InternalErrorCode, "This Service has no secret broker, so there is no master password to forget.");
            return;
        }

        _secrets.Forget();
        WriteResult(writer, id, "ok");
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

    /// <summary>Internal (not private) so a test can register a child without a real handoff.</summary>
    internal void TrackChild(string name, Process? watchHandle)
    {
        if (watchHandle is null)
        {
            Logger.Diagnostic($"host: mcp tool '{name}' had already exited by handoff; not tracking it.", Category);
            return;
        }

        lock (_childrenLock)
        {
            _children.Add(watchHandle);
        }
    }

    /// <summary>
    /// Drops (and disposes) every tracked child that has exited and returns how many are still
    /// alive. Plain loop, iterating backwards so removal doesn't disturb the remaining indexes.
    /// </summary>
    private int PruneChildren()
    {
        lock (_childrenLock)
        {
            for (var i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].HasExited)
                {
                    _children[i].Dispose();
                    _children.RemoveAt(i);
                }
            }

            return _children.Count;
        }
    }

    private void CheckIdle(object? state)
    {
        if (Volatile.Read(ref _inFlightCount) > 0)
        {
            return;
        }

        // Issue #45: a launched tool outlives its handoff, so the idle TTL must not orphan it.
        // While any tool is alive Service counts as active, which also means the TTL restarts
        // from whenever the *last* tool exits rather than from the last request.
        var liveChildren = PruneChildren();
        if (liveChildren > 0)
        {
            if (!_waitingOnChildren)
            {
                _waitingOnChildren = true;
                Logger.Info($"host: {liveChildren} mcp tool(s) still running; idle timeout deferred until they exit.", Category);
            }

            RecordActivity();
            return;
        }

        if (_waitingOnChildren)
        {
            _waitingOnChildren = false;
            Logger.Info("host: last mcp tool exited; idle timeout restarts now.", Category);
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

    private sealed record McpResult(string Stdin, string Stdout);
}
