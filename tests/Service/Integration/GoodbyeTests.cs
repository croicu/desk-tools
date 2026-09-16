using System.Text.Json;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Integration;

/// <summary>
/// Exercises <see cref="McpToolLauncher"/> against the real, build-generated <c>goodbye</c>
/// registry entry and the real <c>scripts/goodbye.py</c> it points at -- the first non-.NET
/// registry entry (see <c>scripts/Scripts.csproj</c>'s own remarks): copied into the shared
/// <c>out/&lt;Configuration&gt;/net10.0/[&lt;RID&gt;]/</c> folder at build time specifically so it
/// lands co-located with <c>Service.exe</c> exactly like a .NET tool's own <c>.dll</c> already
/// does, meaning <see cref="McpToolLauncher"/> needs no Python-specific handling at all -- this
/// test's own existence is really confirming that co-location assumption holds for a Python tool
/// too, not just re-proving the redirected-pipe mechanism <c>tests/Service/Integration/HelloTests.cs</c>
/// already covers. Tagged <see cref="TestCategoryAttribute"/>("Integration") per CLAUDE.md's
/// Architecture convention 4 -- the default `dotnet test` invocation skips this; run it explicitly
/// with `dotnet test --filter TestCategory=Integration` (<c>scripts/Scripts.csproj</c> must
/// already be built, e.g. via a plain `dotnet build`, so <c>goodbye.py</c> and its registry entry
/// actually exist).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class GoodbyeTests
{
    [TestMethod]
    public void Launch_RealGoodbyeEntry_SayGoodbyeReturnsBye()
    {
        var outDir = RepoPaths.ResolveOutDir();
        var registryPath = Path.Combine(outDir, "mcp-registry", "goodbye.json");
        Assert.IsTrue(File.Exists(registryPath), $"'{registryPath}' not found -- build scripts/Scripts.csproj first (e.g. `dotnet build`).");

        using var goodbye = McpToolLauncher.Launch("goodbye", registryBaseDirectory: outDir);

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
