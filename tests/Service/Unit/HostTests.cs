using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Port 0 everywhere so the OS assigns an ephemeral port per instance -- MSTestSettings.cs
/// parallelizes at method level, so a hardcoded shared port would make these tests collide with
/// each other (and with ProgramTests.Main_RunsClean, which exercises the default
/// ISettingsProvider.Port via Program.Run). WaitForIdleShutdown() blocks the calling thread until
/// the idle timer fires, so every call here runs on its own Task with a bounded Wait() -- a real
/// regression (timer never firing) would otherwise hang the test run instead of failing it.
/// </summary>
[TestClass]
public sealed class HostTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

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
    public void AcceptLoop_EchoesOneLineThenCloses()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            // No BOM: a real client (src/Desk's Client) doesn't send one either -- see
            // Host.WriteEncoding's own remarks for why that matters.
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine("echo request");
            var reply = reader.ReadLine();
            Assert.AreEqual("echo request", reply);

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
    public void AcceptLoop_ShutdownCommand_TriggersShutdownWithoutWaitingForIdleTimeout()
    {
        // A long idle timeout: if shutdown here actually waited on the idle timer instead of being
        // triggered directly by the request, the bounded wait below would time out and fail.
        var host = new Host(new TestSettings { IdleTimeout = 600 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine(Host.ShutdownCommand);

            // Still echoed back first, same as any other line -- the client gets a definitive
            // acknowledgment before the listener actually stops.
            var reply = reader.ReadLine();
            Assert.AreEqual(Host.ShutdownCommand, reply);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down promptly after a shutdown request.");
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
