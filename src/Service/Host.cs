using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Resident-process skeleton (see tasks/resident-process-skeleton.md): stays alive accepting TCP
/// connections, echoing back one newline-delimited line of text per connection (see
/// <see cref="HandleConnection"/>), then exits once no connection has been in flight for
/// <see cref="ISettingsProvider.IdleTimeout"/> -- or once a client sends <see cref="ShutdownCommand"/>
/// instead of an ordinary line, triggering the exact same shutdown path early (see issue #22:
/// deliberately shares <see cref="_shutdownSignal"/> with the idle-timeout path rather than growing
/// a second one, since there's nothing else a graceful shutdown needs to do yet). Accepting a
/// connection is the interim activity signal ("Activity tracking (interim)" in the task doc): once
/// real JSON-RPC framing/dispatch lands, the real spec wants the timer reset on completed request,
/// not connection open. The port to listen on comes from <see cref="ISettingsProvider.Port"/>
/// (shared settings.json, so a separate client process -- <c>src/Desk</c> -- can learn the same
/// value independently).
/// </summary>
internal sealed class Host
{
    /// <summary>
    /// Reserved line a client can send instead of an ordinary echo request to ask <see cref="Host"/>
    /// to shut down gracefully (see <see cref="HandleConnection"/>) -- still echoed back first
    /// (same as any other line), so the client gets a definitive acknowledgment before the listener
    /// actually stops. Known limitation of the current "plain text, not real framing yet" protocol
    /// (docs/PROTOCOL.md): an ordinary echo request whose content happens to equal this exact string
    /// also triggers a shutdown -- acceptable today since <c>src/Desk</c> is the only client and
    /// never sends arbitrary user-supplied text, but worth revisiting once real request framing
    /// lands. Duplicated (not shared via a project reference) as <c>Program.ShutdownCommand</c> in
    /// <c>src/Desk</c>, which deliberately has no <c>ProjectReference</c> on <c>Service.csproj</c> --
    /// keep the two literals in sync by hand.
    /// </summary>
    internal const string ShutdownCommand = "shutdown";

    private const string Category = "host";

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
    /// <see cref="ShutdownCommand"/> request -- then stops the listener and joins the accept thread.
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
    /// Named handler (not a lambda) per the task doc's Design decisions -- accept, echo, close,
    /// record activity, and track in-flight count around the handling so <see cref="CheckIdle"/>
    /// never fires mid-connection. Reads at most one newline-delimited line and writes it straight
    /// back (plain text, no framing beyond the trailing newline -- see docs/PROTOCOL.md), then
    /// closes; a client that sends nothing (EOF with no line) gets no reply, just a closed
    /// connection. If the line was <see cref="ShutdownCommand"/>, signals shutdown only after this
    /// connection's own reply has been written and its resources disposed -- the client always gets
    /// its acknowledgment, and the listener never stops mid-write of an unrelated in-flight reply.
    /// </summary>
    private void HandleConnection(TcpClient client)
    {
        Interlocked.Increment(ref _inFlightCount);
        try
        {
            RecordActivity();

            string? line;
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, WriteEncoding) { NewLine = "\n", AutoFlush = true })
            {
                line = reader.ReadLine();
                if (line is not null)
                {
                    writer.WriteLine(line);
                }
            }

            if (line == ShutdownCommand)
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
}
