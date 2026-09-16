using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Mocks;
using Microsoft.Win32.SafeHandles;

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
    /// Exercises issue #32's real end-to-end path: a real <see cref="Host"/> handling a real
    /// <c>mcp</c> JSON-RPC request over its loopback TCP listener, launching the real <c>hello</c>
    /// registry entry, and handing this test process (standing in for a future real caller, e.g.
    /// Desk) direct <c>DuplicateHandle</c>-based access to its stdin/stdout -- then a real
    /// <c>initialize</c> request/response over those duplicated handles, not Host's own pipes.
    /// Windows-only (the actual mechanism is), guarded at runtime with <see cref="Assert.Inconclusive"/>
    /// rather than a compile-time Platform/ split: unlike <c>Unit/Platform/Windows/HandleDuplicatorTests.cs</c>,
    /// this test's other precondition (the real, build-generated <c>hello</c> registry entry) has
    /// nothing to do with the RID <c>Service.Test.csproj</c> itself was built for, so a full
    /// Integration+Platform combined folder structure isn't worth building for one test -- run with
    /// `dotnet test --filter TestCategory=Integration -r win-x64` to actually exercise the real path
    /// (the plain `-r win-x64` without an explicit RID still compiles/runs this method, but Host's
    /// own <c>mcp</c> dispatch would fall through to <c>Platform/Neutral/HandleDuplicator.cs</c>'s
    /// "not supported" throw instead of the real one, since <c>Service.csproj</c> itself wasn't
    /// built for win-x64 in that case).
    /// </summary>
    [TestMethod]
    public void Mcp_RealHelloEntry_DuplicatedHandlesCarryARealInitializeExchange()
    {
        // OperatingSystem.IsWindows() alone isn't enough: it reports the actual OS, not which
        // Platform/ variant got compiled into the referenced Service.dll -- running this test on a
        // real Windows machine but built with no RID still links against
        // Platform/Neutral/HandleDuplicator.cs's "not supported" throw. The RID build lands in a
        // .../net10.0/win-x64/ subfolder (see FindConfigurationFolder's own remarks), so its name is
        // a reliable proxy for "was I actually built for win-x64".
        if (!OperatingSystem.IsWindows() || new DirectoryInfo(AppContext.BaseDirectory).Name != "win-x64")
        {
            Assert.Inconclusive("This test needs the real Windows HandleDuplicator, only compiled into Service.dll when built for win-x64 -- run with `dotnet test tests/Service/Service.Tests.csproj -r win-x64`. See issue #32.");
            return;
        }

        var outDir = ResolveSharedOutDir();
        var registryPath = Path.Combine(outDir, "mcp-registry", "hello.json");
        Assert.IsTrue(File.Exists(registryPath), $"'{registryPath}' not found -- build Hello first (e.g. `dotnet build -r win-x64`).");

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
    /// out/&lt;Configuration&gt;/net10.0/ -- shared by every app project (see
    /// src/Directory.Build.props' BaseOutputPath), so this is where both Hello.dll and its own
    /// generated mcp-registry/hello.json land. Mirrors
    /// tests/Desk/Integration/ServiceTests.cs's own ResolveServiceDllPath/FindRepoRoot approach:
    /// walks up from the test assembly's own directory looking for Service.slnx, robust to the exact
    /// bin/obj folder depth.
    /// </summary>
    private static string ResolveSharedOutDir()
    {
        var configuration = FindConfigurationFolder(new DirectoryInfo(AppContext.BaseDirectory));
        return Path.Combine(FindRepoRoot(), "out", configuration, "net10.0");
    }

    /// <summary>
    /// Walks up looking for a folder literally named "Debug"/"Release", rather than assuming a
    /// fixed parent-hop count -- normally tests/Service/bin/Debug/net10.0/, but
    /// tests/Service/bin/Debug/net10.0/win-x64/ when this test project itself is built with an
    /// explicit RuntimeIdentifier (see <see cref="Mcp_RealHelloEntry_DuplicatedHandlesCarryARealInitializeExchange"/>'s
    /// own remarks on why that's needed here), which would otherwise make the immediate parent's
    /// name "net10.0" instead of the actual configuration.
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
