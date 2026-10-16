using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// How <see cref="ToolLauncher"/> describes the process it starts (without starting one): redirected stdio, the
/// registry's own args and env, the parent-watchdog variable, and -- issue #44 -- no console window unless asked.
/// </summary>
[TestClass]
public sealed class ToolLauncherTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [TestMethod]
    public void CreateStartInfo_DefaultsToNoConsoleWindow()
    {
        var info = ToolLauncher.CreateStartInfo("dotnet", ["Hello.dll"], NoEnv, "C:\\work", showConsole: false);

        Assert.IsTrue(info.CreateNoWindow);
    }

    [TestMethod]
    public void CreateStartInfo_ShowConsole_LeavesTheWindowAlone()
    {
        var info = ToolLauncher.CreateStartInfo("dotnet", ["Hello.dll"], NoEnv, "C:\\work", showConsole: true);

        Assert.IsFalse(info.CreateNoWindow);
    }

    [TestMethod]
    public void CreateStartInfo_RedirectsStdioAndCarriesArgsEnvAndTheWatchdogVariable()
    {
        var env = new Dictionary<string, string> { ["FROM_REGISTRY"] = "1" };

        var info = ToolLauncher.CreateStartInfo("python", ["tests/MCP/Goodbye/goodbye.py", "--x"], env, "C:\\work", showConsole: false);

        Assert.AreEqual("python", info.FileName);
        Assert.AreEqual("C:\\work", info.WorkingDirectory);
        Assert.IsFalse(info.UseShellExecute);
        Assert.IsTrue(info.RedirectStandardInput);
        Assert.IsTrue(info.RedirectStandardOutput);
        CollectionAssert.AreEqual(new[] { "tests/MCP/Goodbye/goodbye.py", "--x" }, info.ArgumentList.ToArray());
        Assert.AreEqual("1", info.Environment["FROM_REGISTRY"]);
        Assert.AreEqual(Environment.ProcessId.ToString(), info.Environment[ParentWatchdog.EnvironmentVariable]);
    }

    [TestMethod]
    public void CreateStartInfo_AFragmentCannotOverrideTheWatchdogVariable()
    {
        var env = new Dictionary<string, string> { [ParentWatchdog.EnvironmentVariable] = "1" };

        var info = ToolLauncher.CreateStartInfo("dotnet", [], env, "C:\\work", showConsole: false);

        Assert.AreEqual(Environment.ProcessId.ToString(), info.Environment[ParentWatchdog.EnvironmentVariable]);
    }
}
