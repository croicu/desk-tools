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
}
