using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Desk.Tests.Unit;

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

    [TestMethod]
    public void Send_NothingListening_ThrowsAppError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new Client(new TestSettings(), port: port);

        Assert.ThrowsExactly<AppError>(() => client.Send("ping"));
    }

    [TestMethod]
    public void LaunchMcpTool_ReturnsHandlesFromResult_AndSendsNameAndOwnProcessId()
    {
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
            writer.WriteLine("""{"jsonrpc": "2.0", "id": 1, "result": {"stdin": "684", "stdout": "688"}}""");
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);
            var (stdin, stdout) = client.LaunchMcpTool("hello");

            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
            Assert.AreEqual(684L, stdin);
            Assert.AreEqual(688L, stdout);

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
    public void LaunchMcpTool_PeerReturnsJsonRpcError_ThrowsAppError()
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

            var error = Assert.ThrowsExactly<AppError>(() => client.LaunchMcpTool("no-such-tool"));
            StringAssert.Contains(error.Message, "no-such-tool");
            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string ExchangeWithPeer(string method, string replyLine)
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
            var reply = client.Send(method);

            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
            return reply;
        }
        finally
        {
            listener.Stop();
        }
    }
}
