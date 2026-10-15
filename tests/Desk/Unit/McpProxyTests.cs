using System.IO.Pipes;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Service.Launcher;

namespace Croicu.Desk.Tools.Desk.Tests.Unit;

/// <summary>
/// Drives <see cref="McpProxy.Run"/> with anonymous pipes standing in for the tool's stdin/stdout
/// (a real <see cref="ToolConnection"/> is built over them by the Launcher from Service's DuplicateHandle handoff;
/// here it is just two streams over the pipes): the test owns the *other* end of
/// each pipe, so it can feed the tool's output, read what reaches the tool's stdin, and "kill the
/// tool" by closing its end. Output is captured through a DiagnosticsLog pointed at a private
/// StringWriter, same technique as Hello's ProgramTests, so nothing touches the real console.
/// </summary>
[TestClass]
public sealed class McpProxyTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    /// <summary>A client input that never ends until released -- like Console.In with a live MCP client.</summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public override string? ReadLine()
        {
            _release.Wait();
            return null;
        }

        public void Release() => _release.Set();
    }

    /// <summary>
    /// The two pipe pairs for one proxy run. <see cref="ToolStdin"/> is what the proxy writes to the
    /// tool (the test reads it); <see cref="ToolStdout"/> is what the tool writes (the test writes it).
    /// The proxy end of each is wrapped in <see cref="Connection"/>, which the test disposes.
    /// </summary>
    private sealed class FakeTool : IDisposable
    {
        private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.In, HandleInheritability.None);
        private readonly AnonymousPipeServerStream _stdout = new(PipeDirection.Out, HandleInheritability.None);

        public FakeTool()
        {
            var proxyWritesHere = new AnonymousPipeClientStream(PipeDirection.Out, _stdin.ClientSafePipeHandle);
            var proxyReadsHere = new AnonymousPipeClientStream(PipeDirection.In, _stdout.ClientSafePipeHandle);
            Connection = new ToolConnection(
                new StreamWriter(proxyWritesHere, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true },
                new StreamReader(proxyReadsHere, Encoding.UTF8));

            ToolStdin = new StreamReader(_stdin, Encoding.UTF8);
            ToolStdout = new StreamWriter(_stdout, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        /// <summary>What <see cref="McpProxy.Run"/> is given: the tool as the proxy side sees it.</summary>
        public ToolConnection Connection { get; }

        public StreamReader ToolStdin { get; }

        public StreamWriter ToolStdout { get; }

        /// <summary>The tool exits: its write end of its own stdout closes, so the proxy reads EOF.</summary>
        public void Die() => ToolStdout.Dispose();

        public void Dispose()
        {
            Connection.Dispose();
            ToolStdin.Dispose();
            ToolStdout.Dispose();
        }
    }

    private static StringWriter CaptureOutput()
    {
        var output = new StringWriter();
        Logger.SetLogger(new DiagnosticsLog(printWriter: output));
        return output;
    }

    [TestMethod]
    public void Run_ToolOutputEndsWhileClientIsStillConnected_ReturnsToolGone()
    {
        CaptureOutput();
        using var tool = new FakeTool();
        var client = new BlockingReader();

        int RunProxy() => McpProxy.Run(tool.Connection, client);

        var proxy = Task.Run(RunProxy);
        tool.Die();

        Assert.IsTrue(proxy.Wait(BoundedWait), "the proxy kept running after its tool went away.");
        Assert.AreEqual(McpProxy.ExitToolGone, proxy.Result);

        client.Release();
    }

    [TestMethod]
    public void Run_ClientInputEnds_ClosesToolStdinAndReturnsClientDisconnected()
    {
        CaptureOutput();
        using var tool = new FakeTool();

        int RunProxy() => McpProxy.Run(tool.Connection, new StringReader(string.Empty));

        var proxy = Task.Run(RunProxy);

        // The tool sees EOF on its stdin, the same signal a real client disconnect gives it.
        Assert.IsNull(tool.ToolStdin.ReadLine());
        tool.Die();

        Assert.IsTrue(proxy.Wait(BoundedWait));
        Assert.AreEqual(McpProxy.ExitClientDisconnected, proxy.Result);
    }

    [TestMethod]
    public void Run_RelaysLinesBothWays()
    {
        var output = CaptureOutput();
        using var tool = new FakeTool();

        int RunProxy() => McpProxy.Run(tool.Connection, new StringReader("line-from-client\n"));

        var proxy = Task.Run(RunProxy);

        Assert.AreEqual("line-from-client", tool.ToolStdin.ReadLine());
        tool.ToolStdout.WriteLine("line-from-tool");

        // The client's input is already exhausted, so the proxy closes the tool's stdin. Wait for the
        // tool to see that EOF before it dies: otherwise, on a slow machine, the tool's output can end
        // first and the proxy reports "tool gone" instead of "client disconnected".
        Assert.IsNull(tool.ToolStdin.ReadLine());
        tool.Die();

        Assert.IsTrue(proxy.Wait(BoundedWait));
        Assert.AreEqual(McpProxy.ExitClientDisconnected, proxy.Result);
        Assert.Contains("line-from-tool", output.ToString());
    }
}
