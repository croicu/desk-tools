using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Desk.Tests.Unit;

/// <summary>
/// Exercises <see cref="Client"/> against a hand-rolled peer (a bare <see cref="TcpListener"/>
/// that echoes back whatever single line it reads) rather than a real <c>Service.Host</c> -- avoids
/// a test-project dependency on <c>Service.csproj</c> just to fake the other end of one TCP
/// exchange. Port 0 everywhere so the OS assigns an ephemeral port per instance -- MSTestSettings.cs
/// parallelizes at method level, so a hardcoded shared port would make these tests collide with each
/// other.
/// </summary>
[TestClass]
public sealed class ClientTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void SendEcho_ReturnsTheLineThePeerEchoesBack()
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

            var line = reader.ReadLine();
            if (line is not null)
            {
                writer.WriteLine(line);
            }
        });

        try
        {
            var client = new Client(new TestSettings(), port: port);
            var reply = client.SendEcho("ping");

            Assert.AreEqual("ping", reply);
            Assert.IsTrue(peerTask.Wait(BoundedWait), "Peer task did not complete within the bounded wait.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void SendEcho_NothingListening_ThrowsAppError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new Client(new TestSettings(), port: port);

        Assert.ThrowsExactly<AppError>(() => client.SendEcho("ping"));
    }
}
