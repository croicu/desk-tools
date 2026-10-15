using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Launcher.Tests.Unit;

/// <summary>
/// <see cref="Starter.EnsureRunning"/>'s decision logic, with the real start (the scheduled task) replaced by a
/// recorder so nothing in the test run ever starts a Service.
/// </summary>
[TestClass]
public sealed class StarterTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    private sealed class StartRecorder
    {
        public int Calls { get; private set; }

        public Func<bool>? IsReachable { get; private set; }

        public void Start(Func<bool> isReachable, TimeSpan? timeout)
        {
            Calls++;
            IsReachable = isReachable;
        }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task AnswerOnce(TcpListener listener, string replyLine)
    {
        void Answer()
        {
            using var peer = listener.AcceptTcpClient();
            using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            reader.ReadLine();
            writer.WriteLine(replyLine);
        }

        return Task.Run(Answer);
    }

    [TestMethod]
    public void EnsureRunning_ServiceAnswersThePing_StartsNothing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = AnswerOnce(listener, """{"jsonrpc": "2.0", "id": 1, "result": "pong"}""");
        var recorder = new StartRecorder();
        try
        {
            Starter.EnsureRunning(new Client(new TestSettings(), port: port), timeout: null, recorder.Start);

            Assert.AreEqual(0, recorder.Calls);
            Assert.IsTrue(peer.Wait(BoundedWait));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void EnsureRunning_NothingListening_StartsServiceAndWaitsForTheClientToReachIt()
    {
        var recorder = new StartRecorder();
        var client = new Client(new TestSettings(), port: ClosedPort());

        Starter.EnsureRunning(client, timeout: TimeSpan.FromSeconds(7), recorder.Start);

        Assert.AreEqual(1, recorder.Calls);
        Assert.IsNotNull(recorder.IsReachable);
        Assert.IsFalse(recorder.IsReachable(), "the reachability check handed to the starter must be the client's own ping.");
    }

    [TestMethod]
    public void EnsureRunning_ServiceAnswersWithAnError_IsNotNotRunning_SoItStartsNothingAndThrows()
    {
        // Something is listening and answered -- starting a second Service would be wrong.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = AnswerOnce(listener, """{"jsonrpc": "2.0", "id": 1, "error": {"code": -32603, "message": "boom"}}""");
        var recorder = new StartRecorder();
        try
        {
            Assert.ThrowsExactly<AppError>(() => Starter.EnsureRunning(new Client(new TestSettings(), port: port), timeout: null, recorder.Start));

            Assert.AreEqual(0, recorder.Calls);
            Assert.IsTrue(peer.Wait(BoundedWait));
        }
        finally
        {
            listener.Stop();
        }
    }
}
