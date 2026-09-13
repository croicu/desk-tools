namespace Croicu.Desk.Tools.Service.Tests.Unit;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void Main_RunsClean()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        var exitCode = Croicu.Desk.Tools.Service.Program.Run(Array.Empty<string>(), settingsPath: settingsPath);
        Assert.AreEqual(0, exitCode);
    }
}
