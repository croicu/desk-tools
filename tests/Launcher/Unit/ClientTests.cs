using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Launcher.Tests.Unit;

/// <summary>
/// Exercises <see cref="Client"/> against a hand-rolled peer (a bare <see cref="TcpListener"/> that
/// reads one JSON-RPC request line and writes back a canned JSON-RPC response) rather than a real
/// <c>Service.Host</c> -- avoids a test-project dependency on <c>Service.csproj</c> just to fake the
/// other end of one TCP exchange. Port 0 everywhere so the OS assigns an ephemeral port per
/// instance -- MSTestSettings.cs parallelizes at method level, so a hardcoded shared port would make
/// these tests collide with each other. See
/// [issue #31](https://github.com/croicu/desk-tools/issues/31) for the switch to JSON-RPC.
/// </summary>
[TestClass]
public sealed class ClientTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Send_ReturnsThePeersResult()
    {
        var reply = ExchangeWithPeer("ping", replyLine: """{"jsonrpc": "2.0", "id": 1, "result": "pong"}""");

        Assert.AreEqual("pong", reply);
    }

    [TestMethod]
    public void Send_PeerReturnsJsonRpcError_ThrowsAppError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var peerTask = Task.Run(() =>
        {
            using var peer = listener.AcceptTcpClient();
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };

            reader.ReadLine();
            writer.WriteLine("""{"jsonrpc": "2.0", "id": 1, "error": {"code": -32601, "message": "Method not found: no-such-method"}}""");
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);

            var error = Assert.ThrowsExactly<AppError>(() => client.Send("no-such-method"));
            StringAssert.Contains(error.Message, "Method not found");
            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Service crashing (or being shut down) while a request is in flight resets the connection. That must come
    /// out as an <see cref="AppError"/> -- a message and exit code 1 -- not an unhandled <see cref="IOException"/>
    /// that kills Desk with a crash report ([issue #78](https://github.com/croicu/desk-tools/issues/78)).
    /// </summary>
    [TestMethod]
    public void Send_ConnectionResetWhileWaitingForTheReply_ThrowsAppErrorNotIOException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        void ResetAfterReadingTheRequest()
        {
            using var peer = listener.AcceptTcpClient();
            using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            reader.ReadLine();

            // Abortive close (RST), like a process that died: the client's pending read fails with an IOException.
            peer.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            peer.Client.Close();
        }

        var server = Task.Run(ResetAfterReadingTheRequest);
        try
        {
            var client = new Client(new TestSettings(), port: port);

            var error = Assert.ThrowsExactly<AppError>(() => client.Send("ping"));

            StringAssert.Contains(error.Message, "lost the connection");
            Assert.IsTrue(server.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void Send_NothingListening_ThrowsAppError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new Client(new TestSettings(), port: port);

        // Refused (nothing listening) is its own type -- Service is simply not running -- but still an AppError.
        var error = Assert.ThrowsExactly<NotRunningError>(() => client.Send("ping"));
        Assert.IsInstanceOfType<AppError>(error);
    }

    [TestMethod]
    public void LaunchTool_OpensTheHandlesFromTheResult_AsAConnectionToTheTool_AndSendsNameAndOwnProcessId()
    {
        // Real pipes stand in for the tool's stdio; Service's handoff would have duplicated their handle values
        // into this process, and the reply carries those values as text.
        using var pipes = new FakeToolPipes();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string? capturedRequest = null;
        var peerTask = Task.Run(() =>
        {
            using var peer = listener.AcceptTcpClient();
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };

            capturedRequest = reader.ReadLine();
            writer.WriteLine($$$"""{"jsonrpc": "2.0", "id": 1, "result": {"stdin": "{{{pipes.StdinHandle}}}", "stdout": "{{{pipes.StdoutHandle}}}"}}""");
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);
            using var tool = client.LaunchTool("hello");

            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");

            // What the caller writes reaches the tool's stdin; what the tool writes comes back out.
            tool.Input.WriteLine("to-the-tool");
            Assert.AreEqual("to-the-tool", pipes.ToolStdin.ReadLine());
            pipes.ToolStdout.WriteLine("from-the-tool");
            Assert.AreEqual("from-the-tool", tool.Output.ReadLine());

            Assert.IsNotNull(capturedRequest);
            StringAssert.Contains(capturedRequest, "\"name\":\"hello\"");
            StringAssert.Contains(capturedRequest, $"\"processId\":{Environment.ProcessId}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void Ping_ReturnsPong()
    {
        Assert.AreEqual("pong", ExchangeWithPeer("ping", replyLine: """{"jsonrpc": "2.0", "id": 1, "result": "pong"}""", call: client => client.Ping()));
    }

    [TestMethod]
    public void IsReachable_NothingListening_IsFalseAndNeverThrows()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.IsFalse(new Client(new TestSettings(), port: port).IsReachable());
    }

    [TestMethod]
    public void LaunchTool_PeerReturnsJsonRpcError_ThrowsAppError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var peerTask = Task.Run(() =>
        {
            using var peer = listener.AcceptTcpClient();
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };

            reader.ReadLine();
            writer.WriteLine("""{"jsonrpc": "2.0", "id": 1, "error": {"code": -32603, "message": "no registered MCP tool named 'no-such-tool'"}}""");
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);

            var error = Assert.ThrowsExactly<AppError>(() => client.LaunchTool("no-such-tool"));
            StringAssert.Contains(error.Message, "no-such-tool");
            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string ExchangeWithPeer(string method, string replyLine, Func<Client, string>? call = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var peerTask = Task.Run(() =>
        {
            using var peer = listener.AcceptTcpClient();
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // No BOM: mirrors what the real Host writes -- see Host.WriteEncoding's own remarks.
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };

            reader.ReadLine();
            writer.WriteLine(replyLine);
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);
            var reply = call is null ? client.Send(method) : call(client);

            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
            return reply;
        }
        finally
        {
            listener.Stop();
        }
    }
}
