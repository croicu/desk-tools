using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// A client that goes away before Service has written its reply must never take Service down
/// ([issue #78](https://github.com/croicu/desk-tools/issues/78)). It is routine, not exotic: since a launch
/// waits for the master-password dialog for as long as the user takes (#66), the client that asked (a Desk
/// proxy an MCP client has since killed, say) is often long gone when the reply is written. The original bug was
/// an unhandled <see cref="IOException"/> on a thread-pool thread, which ends the whole process and every tool it
/// launched.
///
/// A real socket cannot reproduce this reliably (on Windows the first write after the peer's reset silently
/// succeeds and only the second throws), so these tests hand <see cref="Host.ServeConnection"/> a stream that
/// fails the way a dead connection does: before the fix the exception escapes the method.
/// </summary>
[TestClass]
public sealed class HostSurvivesDisconnectTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private sealed class RecordingSink : DiagnosticsLog
    {
        public List<TelemetryRecord> Received { get; } = new();

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            var record = base.Log(level, message, category);
            lock (Received)
            {
                Received.Add(record);
            }

            return record;
        }

        public List<string> Messages()
        {
            var messages = new List<string>();
            lock (Received)
            {
                foreach (var record in Received)
                {
                    messages.Add($"{record.Level}: {record.Message}");
                }
            }

            return messages;
        }

        public int Count(TelemetryLevel level)
        {
            var count = 0;
            lock (Received)
            {
                foreach (var record in Received)
                {
                    if (record.Level == level)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    /// <summary>Serves one request line, then throws whatever it was told to when the host writes (or reads).</summary>
    private sealed class FailingStream : Stream
    {
        private readonly MemoryStream _request;
        private readonly Exception? _writeFailure;
        private readonly Exception? _readFailure;

        public FailingStream(string requestLine, Exception? writeFailure = null, Exception? readFailure = null)
        {
            _request = new MemoryStream(new UTF8Encoding(false).GetBytes(requestLine + "\n"));
            _writeFailure = writeFailure;
            _readFailure = readFailure;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readFailure is not null)
            {
                throw _readFailure;
            }

            return _request.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_writeFailure is not null)
            {
                throw _writeFailure;
            }
        }

        // Like NetworkStream.Flush: a no-op. (A throwing Flush would fire inside StreamWriter's AutoFlush setter, at
        // construction, before any request is read -- not where a real dead connection fails.)
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string Request(string method)
    {
        return "{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"" + method + "\"}";
    }

    private static IOException ConnectionReset()
    {
        return new IOException("Unable to write data to the transport connection: An existing connection was forcibly closed by the remote host.");
    }

    [TestMethod]
    public void ServeConnection_ReplyCannotBeWrittenBecauseTheClientIsGone_DoesNotThrow_AndWarns()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var host = new Host(new TestSettings(), port: 0);

        host.ServeConnection(new FailingStream(Request(Host.PingMethod), writeFailure: ConnectionReset()));

        Assert.AreEqual(1, sink.Count(TelemetryLevel.Warning));
        Assert.AreEqual(0, sink.Count(TelemetryLevel.Error));
    }

    [TestMethod]
    public void ServeConnection_ErrorReplyCannotBeWrittenEither_DoesNotThrow()
    {
        // An unknown method makes the host write an error reply; that write fails too (the dead client).
        var host = new Host(new TestSettings(), port: 0);

        host.ServeConnection(new FailingStream(Request("no-such-method"), writeFailure: ConnectionReset()));
    }

    [TestMethod]
    public void ServeConnection_RequestCannotBeReadBecauseTheClientIsGone_DoesNotThrow()
    {
        var host = new Host(new TestSettings(), port: 0);

        host.ServeConnection(new FailingStream(Request(Host.PingMethod), readFailure: new IOException("reset while reading")));
    }

    [TestMethod]
    public void ServeConnection_SomethingUnexpectedFails_DoesNotThrow_AndLogsAnError()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var host = new Host(new TestSettings(), port: 0);

        host.ServeConnection(new FailingStream(Request(Host.PingMethod), writeFailure: new InvalidOperationException("boom")));

        Assert.AreEqual(1, sink.Count(TelemetryLevel.Error));
    }

    [TestMethod]
    public void ServeConnection_ShutdownWhoseReplyCannotBeDelivered_StillShutsTheHostDown()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var host = new Host(new TestSettings { IdleTimeout = 600 }, port: 0);
        host.Start();

        host.ServeConnection(new FailingStream(Request(Host.ShutdownMethod), writeFailure: ConnectionReset()));

        var shutDown = Task.Run(host.WaitForIdleShutdown).Wait(BoundedWait);
        Assert.IsTrue(shutDown, "a shutdown request must be honored even when its acknowledgment cannot be delivered. Log: " + string.Join(" | ", sink.Messages()));
    }

    [TestMethod]
    public void ServeConnection_AfterAFailedConnection_TheHostStillServesTheNextOne()
    {
        var host = new Host(new TestSettings(), port: 0);
        host.ServeConnection(new FailingStream(Request(Host.PingMethod), writeFailure: ConnectionReset()));

        var output = new MemoryStream();
        host.ServeConnection(new ReplyCapturingStream(Request(Host.PingMethod), output));

        StringAssert.Contains(Encoding.UTF8.GetString(output.ToArray()), "pong");
    }

    /// <summary>A healthy connection: serves one request line and captures what the host writes back.</summary>
    private sealed class ReplyCapturingStream : Stream
    {
        private readonly MemoryStream _request;
        private readonly MemoryStream _reply;

        public ReplyCapturingStream(string requestLine, MemoryStream reply)
        {
            _request = new MemoryStream(new UTF8Encoding(false).GetBytes(requestLine + "\n"));
            _reply = reply;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _request.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _reply.Write(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
