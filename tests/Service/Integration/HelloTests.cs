using System.Text.Json;

namespace Croicu.Desk.Tools.Service.Tests.Integration;

/// <summary>
/// Exercises <see cref="McpToolLauncher"/> against the real, build-generated <c>hello</c> registry
/// entry (<c>out/&lt;Configuration&gt;/net10.0/mcp-registry/hello.json</c>, see issue #29) and the
/// real <c>Hello.exe</c>/<c>Hello.dll</c> it points at -- confirms the redirected stdin/stdout pipes
/// actually carry working MCP traffic, not just that a process object exists (issue #30's own
/// verification plan). Named for its counterpart (<c>Hello</c>), not repeating "Service" -- this
/// already lives under Service's own test tree, same reasoning as <c>Client</c> (not
/// <c>DeskClient</c>) in CLAUDE.md's Coding Style, and precedent set by
/// <c>tests/Desk/Integration/ServiceTests.cs</c>. Stays under <c>tests/Service</c> rather than
/// requiring a <c>ProjectReference</c> on <c>Hello.csproj</c> -- <see cref="McpToolLauncher"/>'s own
/// <c>registryBaseDirectory</c> testing seam is enough to point it at the real output folder,
/// no build-time coupling needed. Tagged <see cref="TestCategoryAttribute"/>("Integration") per
/// CLAUDE.md's Architecture convention 4 -- the default `dotnet test` invocation
/// (`--filter TestCategory!=Integration`) skips this; run it explicitly with
/// `dotnet test --filter TestCategory=Integration` (Hello.csproj must already be built, so its own
/// mcp-registry/hello.json and Hello.dll actually exist -- this doesn't build it itself,
/// deliberately staying decoupled rather than adding a ProjectReference just to resolve a path).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class HelloTests
{
    [TestMethod]
    public void Launch_RealHelloEntry_InitializeRequestGetsARealResponse()
    {
        var outDir = ResolveSharedOutDir();
        var registryPath = Path.Combine(outDir, "mcp-registry", "hello.json");
        Assert.IsTrue(File.Exists(registryPath), $"'{registryPath}' not found -- build Hello first (e.g. `dotnet build`).");

        using var hello = McpToolLauncher.Launch("hello", registryBaseDirectory: outDir);

        hello.StandardInput.WriteLine("""{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}""");
        hello.StandardInput.Flush();

        var response = hello.StandardOutput.ReadLine();
        Assert.IsFalse(string.IsNullOrWhiteSpace(response), "expected a response line from hello's stdout.");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.AreEqual(1, root.GetProperty("id").GetInt32());
        Assert.AreEqual("2025-06-18", root.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.AreEqual("hello", root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    /// <summary>
    /// out/&lt;Configuration&gt;/net10.0/ -- shared by every app project (see
    /// src/Directory.Build.props' BaseOutputPath), so this is where both Hello.dll and its own
    /// generated mcp-registry/hello.json land. Mirrors
    /// tests/Desk/Integration/ServiceTests.cs's own ResolveServiceDllPath/FindRepoRoot approach:
    /// walks up from the test assembly's own directory looking for Service.slnx, robust to the exact
    /// bin/obj folder depth.
    /// </summary>
    private static string ResolveSharedOutDir()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(FindRepoRoot(), "out", configuration, "net10.0");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Service.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException($"Could not locate the repo root (Service.slnx) above '{AppContext.BaseDirectory}'.");
        }

        return dir.FullName;
    }
}
