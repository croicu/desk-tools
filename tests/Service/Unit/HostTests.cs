using System.Net;
using System.Net.Sockets;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Port 0 everywhere so the OS assigns an ephemeral port per instance -- MSTestSettings.cs
/// parallelizes at method level, so a hardcoded shared port would make these tests collide with
/// each other (and with ProgramTests.Main_RunsClean, which exercises Host.DefaultPort via
/// Program.Run). WaitForIdleShutdown() blocks the calling thread until the idle timer fires, so
/// every call here runs on its own Task with a bounded Wait() -- a real regression (timer never
/// firing) would otherwise hang the test run instead of failing it.
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
    public void AcceptLoop_AcceptsConnectionAndClosesIt()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);
        host.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);

            // The server closes immediately after accepting (no payload handling yet -- see the
            // task doc's interim activity-tracking design) -- a blocking read observes that as EOF
            // (0 bytes) rather than throwing.
            var buffer = new byte[1];
            var bytesRead = client.GetStream().Read(buffer, 0, buffer.Length);
            Assert.AreEqual(0, bytesRead);
        }

        var shutdownTask = Task.Run(host.WaitForIdleShutdown);
        Assert.IsTrue(shutdownTask.Wait(BoundedWait), "Host did not shut down within the bounded wait after the connection closed.");
    }
}
