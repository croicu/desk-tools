namespace Croicu.Desk.Tools.Service.Tests.Unit;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void Start_RunsClean()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        var exitCode = Croicu.Desk.Tools.Service.Program.Start(Array.Empty<string>(), settingsPath: settingsPath);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void ParseArgs_Log_SetsLogDir()
    {
        var arguments = Program.ParseArgs(["--log", "/var/log/desk-tools"]);

        Assert.AreEqual("/var/log/desk-tools", arguments.LogDir);
    }

    [TestMethod]
    public void ParseArgs_NoLog_LogDirIsNull()
    {
        var arguments = Program.ParseArgs([]);

        Assert.IsNull(arguments.LogDir);
    }
}
