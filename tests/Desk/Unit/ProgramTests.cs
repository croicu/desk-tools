namespace Croicu.Desk.Tools.Desk.Tests.Unit;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void ParseArgs_Log_SetsLogDir()
    {
        var arguments = Program.ParseArgs(["--log", "/var/log/desk"]);

        Assert.AreEqual("/var/log/desk", arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_NoLog_LogDirIsNull()
    {
        var arguments = Program.ParseArgs([]);

        Assert.IsNull(arguments.LogDir);
    }
}
