using System.Net;
using System.Net.Sockets;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Chunk-0 resident-process skeleton (see tasks/resident-process-skeleton.md): stays alive
/// accepting -- and immediately closing -- TCP connections, then exits once no connection has been
/// in flight for <see cref="ISettingsProvider.IdleTimeout"/>. Accepting a connection is the
/// interim activity signal ("Activity tracking (interim)" in the task doc): once real JSON-RPC
/// framing/dispatch lands, the real spec wants the timer reset on completed request, not connection
/// open.
/// </summary>
internal sealed class Host
{
    // Placeholder only -- the real shared port for say_hello gets finalized once framing lands (see
    // the task doc's Design decisions). Exists purely so the accept loop has something to listen on
    // for this chunk.
    internal const int DefaultPort = 51823;

    private const string Category = "host";

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

    public Host(ISettingsProvider settings, int port = DefaultPort)
    {
        _settings = settings;
        _listener = new TcpListener(IPAddress.Loopback, port);
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
    /// Blocks the calling thread until the idle-check timer decides the process has been idle long
    /// enough to shut down, then stops the listener and joins the accept thread -- by the time this
    /// returns, the listener is closed and no thread is left running.
    /// </summary>
    internal void WaitForIdleShutdown()
    {
        _shutdownSignal.Wait();

        _accepting = false;
        _idleTimer?.Dispose();
        _listener.Stop();
        _acceptThread?.Join();

        Logger.Info("host: idle timeout reached; shutting down.", Category);
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
    /// Named handler (not a lambda) per the task doc's Design decisions -- accept, close, record
    /// activity, and track in-flight count around the handling so <see cref="CheckIdle"/> never
    /// fires mid-connection.
    /// </summary>
    private void HandleConnection(TcpClient client)
    {
        Interlocked.Increment(ref _inFlightCount);
        try
        {
            RecordActivity();
            client.Close();
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
