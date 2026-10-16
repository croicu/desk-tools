using System.Text.Json;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Integration;

/// <summary>
/// Exercises <see cref="McpToolLauncher"/> against the real <c>goodbye</c> test fixture
/// (<c>tests/MCP/Goodbye/goodbye.py</c>, issue #86: no longer a shipped tool, so a throwaway registry pointing at
/// the script stands in, see <see cref="FixtureRegistry"/>) -- the Python flavor of the same end-to-end check
/// <c>tests/Service/Integration/HelloTests.cs</c> makes for a .NET tool: <see cref="McpToolLauncher"/> needs no
/// Python-specific handling at all, and a Python tool's redirected stdio carries real MCP traffic too. Tagged
/// <see cref="TestCategoryAttribute"/>("Integration") per CLAUDE.md's Architecture convention 4 -- the default
/// `dotnet test` invocation skips this; run it explicitly with `dotnet test --filter TestCategory=Integration`
/// (needs <c>python</c> on the PATH).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class GoodbyeTests
{
    [TestMethod]
    public void Launch_RealGoodbyeEntry_SayGoodbyeReturnsBye()
    {
        var script = Path.Combine(RepoPaths.FindRepoRoot(), "tests", "MCP", "Goodbye", "goodbye.py");
        Assert.IsTrue(File.Exists(script), $"'{script}' not found.");
        var registryDir = FixtureRegistry.Create("goodbye", "python", script);

        using var goodbye = McpToolLauncher.Launch("goodbye", registryBaseDirectory: registryDir);

        goodbye.StandardInput.WriteLine("""{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}""");
        goodbye.StandardInput.Flush();

        var initializeResponse = goodbye.StandardOutput.ReadLine();
        Assert.IsFalse(string.IsNullOrWhiteSpace(initializeResponse), "expected a response line from goodbye's stdout.");

        using (var doc = JsonDocument.Parse(initializeResponse!))
        {
            var root = doc.RootElement;
            Assert.AreEqual(1, root.GetProperty("id").GetInt32());
            Assert.AreEqual("2025-06-18", root.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.AreEqual("goodbye", root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        }

        goodbye.StandardInput.WriteLine("""{"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "say_goodbye"}}""");
        goodbye.StandardInput.Flush();

        var toolCallResponse = goodbye.StandardOutput.ReadLine();
        Assert.IsFalse(string.IsNullOrWhiteSpace(toolCallResponse), "expected a response line from goodbye's stdout.");

        using var toolCallDoc = JsonDocument.Parse(toolCallResponse!);
        var toolCallRoot = toolCallDoc.RootElement;
        Assert.AreEqual(2, toolCallRoot.GetProperty("id").GetInt32());
        Assert.IsFalse(toolCallRoot.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.AreEqual("Bye from MCP", toolCallRoot.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }
}
