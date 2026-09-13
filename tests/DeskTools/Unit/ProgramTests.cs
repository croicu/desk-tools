namespace DeskTools.Tests.Unit;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void Main_RunsClean()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        var exitCode = DeskTools.Program.Run(Array.Empty<string>(), settingsPath: settingsPath);
        Assert.AreEqual(0, exitCode);
    }
}
