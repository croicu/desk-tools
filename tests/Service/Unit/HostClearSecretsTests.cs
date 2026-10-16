using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Mocks;
using Croicu.Desk.Tools.Secrets;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// The <c>clear-secrets</c> method ([issue #84](https://github.com/croicu/desk-tools/issues/84)): only Service can
/// delete the remembered master password (the file nothing unelevated may touch), so <c>desk clear-secrets</c> asks it to.
/// </summary>
[TestClass]
public sealed class HostClearSecretsTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private sealed class CountingBroker : ISecretBroker
    {
        public int Forgets { get; private set; }

        public IReadOnlyDictionary<string, string> Read(string entry, IReadOnlyList<string> keys) => throw new NotSupportedException();

        public void Forget() => Forgets++;

        public void Dispose()
        {
        }
    }

    private static JsonElement Call(Host host)
    {
        host.Start();
        JsonElement reply;
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, host.Port);
            using var writer = new StreamWriter(client.GetStream(), WriteEncoding) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

            writer.WriteLine($$"""{"jsonrpc": "2.0", "id": 1, "method": "{{Host.ClearSecretsMethod}}"}""");
            var line = reader.ReadLine();
            Assert.IsNotNull(line);
            using var doc = JsonDocument.Parse(line);
            reply = doc.RootElement.Clone();
        }

        Assert.IsTrue(Task.Run(host.WaitForIdleShutdown).Wait(BoundedWait), "the host did not shut down afterwards.");
        return reply;
    }

    [TestMethod]
    public void ClearSecrets_AsksTheSecretBrokerToForget_AndAnswersOk()
    {
        var broker = new CountingBroker();
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0, secrets: broker);

        var reply = Call(host);

        Assert.AreEqual("ok", reply.GetProperty("result").GetString());
        Assert.AreEqual(1, broker.Forgets);
    }

    [TestMethod]
    public void ClearSecrets_WithoutASecretBroker_IsAnErrorNotACrash()
    {
        var host = new Host(new TestSettings { IdleTimeout = 1 }, port: 0);

        var reply = Call(host);

        Assert.AreEqual(-32603, reply.GetProperty("error").GetProperty("code").GetInt32());
        StringAssert.Contains(reply.GetProperty("error").GetProperty("message").GetString(), "no secret broker");
    }
}
