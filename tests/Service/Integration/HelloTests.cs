using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Mocks;
using Microsoft.Win32.SafeHandles;

namespace Croicu.Desk.Tools.Service.Tests.Integration;

/// <summary>
/// Exercises <see cref="McpToolLauncher"/> against the real, build-generated <c>hello</c> registry
/// entry (<c>out/&lt;Configuration&gt;/net10.0/[win-x64/]mcp-registry/hello.json</c>, see issue #29)
/// and the real <c>Hello.exe</c>/<c>Hello.dll</c> it points at -- confirms the redirected
/// stdin/stdout pipes actually carry working MCP traffic, not just that a process object exists
/// (issue #30's own verification plan). Named for its counterpart (<c>Hello</c>), not repeating
/// "Service" -- this already lives under Service's own test tree, same reasoning as <c>Client</c>
/// (not <c>DeskClient</c>) in CLAUDE.md's Coding Style, and precedent set by
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
        var outDir = ResolveOutDir();
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
    /// Exercises issue #32's real end-to-end path: a real <see cref="Host"/> handling a real
    /// <c>mcp</c> JSON-RPC request over its loopback TCP listener, launching the real <c>hello</c>
    /// registry entry, and handing this test process (standing in for a future real caller, e.g.
    /// Desk) direct <c>DuplicateHandle</c>-based access to its stdin/stdout -- then a real
    /// <c>initialize</c> request/response over those duplicated handles, not Host's own pipes.
    /// Windows-only (the actual mechanism is), guarded at runtime with <see cref="Assert.Inconclusive"/>
    /// rather than a compile-time Platform/ split: this test's other precondition (the real,
    /// build-generated <c>hello</c> registry entry) has nothing to do with the RID
    /// <c>Service.Tests.csproj</c> itself was built for, so a full Integration+Platform combined
    /// folder structure isn't worth building for one test. win-x64 is src/Directory.Build.props'
    /// own default now, so this passes on a plain `dotnet test` -- the guard only actually fires if
    /// someone deliberately overrides to a different RID (where <c>Service.dll</c> would link
    /// against <c>Platform/Neutral/HandleDuplicator.cs</c>'s "not supported" throw instead).
    /// </summary>
    [TestMethod]
    public void Mcp_RealHelloEntry_DuplicatedHandlesCarryARealInitializeExchange()
    {
        var outDir = ResolveOutDir();
        if (!string.Equals(new DirectoryInfo(outDir).Name, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"This test needs the real Windows HandleDuplicator -- win-x64 is src/Directory.Build.props' own default, but this build resolved to '{outDir}'. See issue #32.");
            return;
        }

        var registryPath = Path.Combine(outDir, "mcp-registry", "hello.json");
        Assert.IsTrue(File.Exists(registryPath), $"'{registryPath}' not found -- build Hello first (e.g. `dotnet build`).");

        var host = new Host(new TestSettings { IdleTimeout = 5 }, port: 0, mcpRegistryBaseDirectory: outDir);
        host.Start();

        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, host.Port);

            using var requestWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };
            using var responseReader = new StreamReader(client.GetStream(), Encoding.UTF8);

            requestWriter.WriteLine($$"""{"jsonrpc": "2.0", "id": 1, "method": "{{Host.McpMethod}}", "params": {"name": "hello", "processId": {{Environment.ProcessId}} } }""");

            var mcpReply = responseReader.ReadLine();
            Assert.IsNotNull(mcpReply, "expected an 'mcp' response line from Host.");

            using var mcpDoc = JsonDocument.Parse(mcpReply);
            var result = mcpDoc.RootElement.GetProperty("result");
            var stdinHandleValue = (nint)long.Parse(result.GetProperty("stdin").GetString()!);
            var stdoutHandleValue = (nint)long.Parse(result.GetProperty("stdout").GetString()!);

            using var stdinHandle = new SafeFileHandle(stdinHandleValue, ownsHandle: true);
            using var stdoutHandle = new SafeFileHandle(stdoutHandleValue, ownsHandle: true);

            using (var writeStream = new FileStream(stdinHandle, FileAccess.Write))
            using (var helloWriter = new StreamWriter(writeStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true })
            using (var readStream = new FileStream(stdoutHandle, FileAccess.Read))
            using (var helloReader = new StreamReader(readStream, Encoding.UTF8))
            {
                helloWriter.WriteLine("""{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}""");

                var helloReply = helloReader.ReadLine();
                Assert.IsFalse(string.IsNullOrWhiteSpace(helloReply), "expected a response line from hello's stdout, read through the duplicated handle.");

                using var helloDoc = JsonDocument.Parse(helloReply!);
                var helloRoot = helloDoc.RootElement;
                Assert.AreEqual(1, helloRoot.GetProperty("id").GetInt32());
                Assert.AreEqual("2025-06-18", helloRoot.GetProperty("result").GetProperty("protocolVersion").GetString());
                Assert.AreEqual("hello", helloRoot.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            }
            // helloWriter's Dispose (above) closed this test's own copy of hello's stdin -- Service
            // already closed its own copy inside HandleDuplicator.Duplicate, so hello now sees EOF
            // on every write-end handle and exits its own read loop gracefully.
        }
        finally
        {
            // Bounded, not a direct blocking call -- a real regression (idle timer never firing)
            // should fail this test, not hang the whole run indefinitely (same defensive pattern
            // Unit/HostTests.cs's own shutdown assertions already use).
            Assert.IsTrue(Task.Run(host.WaitForIdleShutdown).Wait(TimeSpan.FromSeconds(15)), "Host did not shut down within the bounded wait.");
        }
    }

    /// <summary>
    /// Mirrors this test assembly's own <c>net10.0[/&lt;RID&gt;]</c> structure onto
    /// <c>out/&lt;Configuration&gt;/net10.0/</c>, since src/Directory.Build.props and
    /// tests/Directory.Build.props apply the identical default <c>RuntimeIdentifier</c> (win-x64
    /// today -- see src/Directory.Build.props' own remarks) -- whatever RID segment (if any) this
    /// test itself landed under is exactly what Hello.csproj's own build landed under too, so this
    /// stays correct if that default ever changes or gets overridden (e.g. `dotnet test -r
    /// linux-x64`), rather than hardcoding "win-x64" as a literal path segment. Mirrors
    /// tests/Desk/Integration/ServiceTests.cs's own identical helper.
    /// </summary>
    private static string ResolveOutDir()
    {
        var testDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? ridSegment = null;
        if (!string.Equals(testDir.Name, "net10.0", StringComparison.OrdinalIgnoreCase))
        {
            ridSegment = testDir.Name;
            testDir = testDir.Parent!;
        }

        var configuration = FindConfigurationFolder(testDir);
        var outDir = Path.Combine(FindRepoRoot(), "out", configuration, "net10.0");
        return ridSegment is null ? outDir : Path.Combine(outDir, ridSegment);
    }

    /// <summary>
    /// Walks up looking for a folder literally named "Debug"/"Release", rather than assuming a
    /// fixed parent-hop count -- robust regardless of how many RID/net10.0 segments sit above it.
    /// </summary>
    private static string FindConfigurationFolder(DirectoryInfo start)
    {
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (dir.Name is "Debug" or "Release")
            {
                return dir.Name;
            }
        }

        throw new InvalidOperationException($"Could not determine the build configuration from '{start.FullName}'.");
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
