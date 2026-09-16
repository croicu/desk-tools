using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Port 0 everywhere so the OS assigns an ephemeral port per instance -- MSTestSettings.cs
/// parallelizes at method level, so a hardcoded shared port would make these tests collide with
/// each other (and with ProgramTests.Main_RunsClean, which exercises the default
/// ISettingsProvider.Port via Program.Run). WaitForIdleShutdown() blocks the calling thread until
/// the idle timer fires, so every call here runs on its own Task with a bounded Wait() -- a real
/// regression (timer never firing) would otherwise hang the test run instead of failing it. See
/// [issue #31](https://github.com/croicu/desk-tools/issues/31) for the switch to JSON-RPC.
/// </summary>
[TestClass]
public sealed class HostTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    // No BOM: a real client (src/Desk's Client) doesn't send one either -- see
    // Host.WriteEncoding's own remarks for why that matters.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [TestMethod]
    public void WaitForIdleShutdown_NoActivity_ReturnsAfterIdleTimeout()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);

        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait.");
    }

    [TestMethod]
    public void WaitForIdleShutdown_StopsListener_SoLaterConnectionsAreRefused()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();
        var port = host.Port;

        Task.Run(host.WaitForIdleShutdown).Wait(BoundedWait);

        Assert.ThrowsExactly<SocketException>(() =>
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
        });
    }

    [TestMethod]
    public void AcceptLoop_Ping_ReturnsPongThenCloses()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine($$"""{"jsonrpc": "2.0", "id": 1, "method": "{{Host.PingMethod}}"}""");
            var reply = reader.ReadLine();
            Assert.IsNotNull(reply);

            using var doc = JsonDocument.Parse(reply);
            Assert.AreEqual("pong", doc.RootElement.GetProperty("result").GetString());

            // The server closes right after replying -- a further blocking read observes that as
            // EOF (0 bytes) rather than throwing.
            var buffer = new byte[1];
            var bytesRead = client.GetStream().Read(buffer, 0, buffer.Length);
            Assert.AreEqual(0, bytesRead);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }

    [TestMethod]
    public void AcceptLoop_Shutdown_TriggersShutdownWithoutWaitingForIdleTimeout()
    {
        // A long idle timeout: if shutdown here actually waited on the idle timer instead of being
        // triggered directly by the request, the bounded wait below would time out and fail.
        var host = new Host(new TestSettings { IdleTimeout = 600 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine($$"""{"jsonrpc": "2.0", "id": 1, "method": "{{Host.ShutdownMethod}}"}""");

            // Still replied to first, same as any other request -- the client gets a definitive
            // acknowledgment before the listener actually stops.
            var reply = reader.ReadLine();
            Assert.IsNotNull(reply);
            using var doc = JsonDocument.Parse(reply);
            Assert.AreEqual("ok", doc.RootElement.GetProperty("result").GetString());
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down promptly after a shutdown request.");
    }

    [TestMethod]
    public void AcceptLoop_UnknownMethod_ReturnsMethodNotFoundError()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine("""{"jsonrpc": "2.0", "id": 1, "method": "no-such-method"}""");
            var reply = reader.ReadLine();
            Assert.IsNotNull(reply);

            using var doc = JsonDocument.Parse(reply);
            Assert.AreEqual(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }

    [TestMethod]
    public void AcceptLoop_MalformedJson_ReturnsParseError()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine("not valid json");
            var reply = reader.ReadLine();
            Assert.IsNotNull(reply);

            using var doc = JsonDocument.Parse(reply);
            Assert.AreEqual(-32700, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }

    [TestMethod]
    public void AcceptLoop_NotificationWithNoId_ClosesWithNoReply()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };

            writer.WriteLine($$"""{"jsonrpc": "2.0", "method": "{{Host.PingMethod}}"}""");

            // No id -- a notification, per JSON-RPC. Consumed, no reply, regardless of method.
            var buffer = new byte[1];
            var bytesRead = client.GetStream().Read(buffer, 0, buffer.Length);
            Assert.AreEqual(0, bytesRead);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }

    [TestMethod]
    public void AcceptLoop_ConnectionWithNoLine_ClosesWithNoReply()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            client.Client.Shutdown(SocketShutdown.Send);

            // No line was ever sent, so the server writes no reply -- a blocking read observes
            // EOF (0 bytes) once it closes its own end.
            var buffer = new byte[1];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            Assert.AreEqual(0, bytesRead);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }
}
