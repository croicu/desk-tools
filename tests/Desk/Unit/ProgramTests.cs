using System.Net;
using System.Net.Sockets;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Desk.Tests.Unit;

/// <summary>
/// Only exercises ParseArgs's success paths -- same boundary Service's/Hello's own ParseArgs tests
/// already keep: its error paths call Environment.Exit, which would tear down the test process
/// itself if invoked in-process, so they're not unit-testable here.
/// </summary>
[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void ParseArgs_Ping_SetsCommand()
    {
        var arguments = Program.ParseArgs(["ping"]);

        Assert.AreEqual(DeskCommand.Ping, arguments.Command);
        Assert.IsNull(arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_Shutdown_SetsCommand()
    {
        var arguments = Program.ParseArgs(["shutdown"]);

        Assert.AreEqual(DeskCommand.Shutdown, arguments.Command);
    }

    [TestMethod]
    public void ParseArgs_Log_SetsLogDir()
    {
        var arguments = Program.ParseArgs(["ping", "--log", "/var/log/desk"]);

        Assert.AreEqual(DeskCommand.Ping, arguments.Command);
        Assert.AreEqual("/var/log/desk", arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_LogBeforeCommand_StillSetsBoth()
    {
        var arguments = Program.ParseArgs(["--log", "/var/log/desk", "shutdown"]);

        Assert.AreEqual(DeskCommand.Shutdown, arguments.Command);
        Assert.AreEqual("/var/log/desk", arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_Mcp_SetsCommandAndToolName()
    {
        var arguments = Program.ParseArgs(["mcp", "hello"]);

        Assert.AreEqual(DeskCommand.Mcp, arguments.Command);
        Assert.AreEqual("hello", arguments.McpToolName);
    }

    [TestMethod]
    public void ParseArgs_McpWithRegistry_SetsRegistryDir()
    {
        var arguments = Program.ParseArgs(["mcp", "echo-secret", "--registry", "/tmp/reg"]);

        Assert.AreEqual(DeskCommand.Mcp, arguments.Command);
        Assert.AreEqual("echo-secret", arguments.McpToolName);
        Assert.AreEqual("/tmp/reg", arguments.RegistryDir);
    }

    [TestMethod]
    public void ParseArgs_McpWithoutRegistry_LeavesRegistryDirNull()
    {
        Assert.IsNull(Program.ParseArgs(["mcp", "hello"]).RegistryDir);
    }

    [TestMethod]
    public void ParseArgs_McpWithLog_SetsAllThree()
    {
        var arguments = Program.ParseArgs(["mcp", "hello", "--log", "/var/log/desk"]);

        Assert.AreEqual(DeskCommand.Mcp, arguments.Command);
        Assert.AreEqual("hello", arguments.McpToolName);
        Assert.AreEqual("/var/log/desk", arguments.LogDir);
    }

    // --- desk shutdown ---

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Runs <see cref="Program.Shutdown"/> with the given client and returns its exit code and printed output.</summary>
    private static (int ExitCode, string Output) RunShutdown(Client client)
    {
        var output = new StringWriter();
        Logger.SetLogger(new DiagnosticsLog(printWriter: output));
        try
        {
            return (Program.Shutdown(client), output.ToString());
        }
        finally
        {
            Logger.SetLogger(null);
        }
    }

    [TestMethod]
    public void Shutdown_ServiceNotRunning_SaysSoAndExitsZeroInsteadOfFailing()
    {
        var (exitCode, output) = RunShutdown(new Client(new TestSettings(), port: ClosedPort()));

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "not running");
        StringAssert.Contains(output, "nothing to shut down");
    }

    [TestMethod]
    public void Shutdown_ServiceAnswers_ConfirmsAndExitsZero()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        void Answer()
        {
            using var peer = listener.AcceptTcpClient();
            using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            reader.ReadLine();
            writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"ok\"}");
        }

        var server = Task.Run(Answer);
        try
        {
            var (exitCode, output) = RunShutdown(new Client(new TestSettings(), port: port));

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(output, "Shutdown requested.");
            Assert.IsTrue(server.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void Shutdown_ConnectionBreaksMidRequest_IsStillAnError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        void ResetAfterReadingTheRequest()
        {
            using var peer = listener.AcceptTcpClient();
            using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            reader.ReadLine();
            peer.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            peer.Client.Close();
        }

        var server = Task.Run(ResetAfterReadingTheRequest);
        try
        {
            // Not "Service is not running": something did answer, then the connection broke -- a real failure.
            Assert.ThrowsExactly<AppError>(() => RunShutdown(new Client(new TestSettings(), port: port)));
            Assert.IsTrue(server.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            listener.Stop();
        }
    }
}
