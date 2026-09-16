using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Croicu.Desk.Tools.Desk.Tests.Integration;

/// <summary>
/// Exercises the real Desk CLI (<see cref="Program.Start"/>) against a real <c>Service</c> instance
/// this test starts itself, by launching <c>out/&lt;Configuration&gt;/net10.0/[win-x64/]Service.dll</c>
/// as a plain child process -- no shared test-framework setup/fixture (no
/// <c>[ClassInitialize]</c>/<c>[AssemblyInitialize]</c>), just imperative start/use/stop inside the
/// one test method, and no reliance on a human having started <c>Service</c> beforehand. Named for
/// its counterpart (<c>Service</c>), not repeating "Desk" -- this already lives under Desk's own
/// test tree (<c>Croicu.Desk.Tools.Desk.Tests.Integration</c>), same reasoning as
/// <c>Client</c> (not <c>DeskClient</c>) in CLAUDE.md's Coding Style. Stays under
/// <c>tests/Desk</c> rather than moving to <c>tests/Service/Integration</c> specifically to avoid
/// giving <c>Service.Tests</c> a <c>ProjectReference</c> on <c>Desk.csproj</c> -- the same
/// no-cross-app-coupling boundary <c>Desk</c> itself already keeps with <c>Service.csproj</c> (see
/// <see cref="Client"/>'s own remarks). See CLAUDE.md's Architecture convention 4 -- tagged
/// <see cref="TestCategoryAttribute"/>("Integration") so the default `dotnet test` invocation
/// (`--filter TestCategory!=Integration`) skips this; run it explicitly with
/// `dotnet test --filter TestCategory=Integration` (Service.csproj must already be built -- this
/// doesn't build it itself, deliberately staying decoupled from Service.csproj rather than adding a
/// ProjectReference just to resolve a path). <see cref="DoNotParallelizeAttribute"/>: every test
/// here starts its own real Service instance on the same, un-overridden default port (there's no
/// port-override CLI flag on Service to give each test its own) -- confirmed as a genuine race, not
/// a theoretical one, by running the two tests in this class together repeatedly: with
/// <see cref="Mcp_LaunchesRealHelloEntry_ProxiesARealInitializeExchange"/> added alongside
/// <see cref="Ping_ThenShutdown_AgainstAManuallyStartedService"/>, MSTestSettings.cs's default
/// method-level parallelization made both tests' Service instances race for the same port, and
/// whichever one lost (or got torn down mid-flight by the other test's own shutdown/kill) produced
/// nondeterministic failures.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ServiceTests
{
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Ping_ThenShutdown_AgainstAManuallyStartedService()
    {
        var serviceDllPath = Path.Combine(ResolveOutDir(), "Service.dll");
        Assert.IsTrue(File.Exists(serviceDllPath), $"Service.dll not found at '{serviceDllPath}' -- build Service first (e.g. `dotnet build`).");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(serviceDllPath),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serviceDllPath);

        using var service = Process.Start(startInfo);
        Assert.IsNotNull(service, "Failed to start Service.");

        try
        {
            WaitUntilReachable();

            var shutdownExitCode = Program.Start(["shutdown"]);
            Assert.AreEqual(0, shutdownExitCode, "shutdown failed.");

            Assert.IsTrue(service.WaitForExit((int)ExitWait.TotalMilliseconds), "Service did not exit within the bounded wait after the shutdown request.");
        }
        finally
        {
            if (!service.HasExited)
            {
                service.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Exercises <c>desk mcp &lt;name&gt;</c> end-to-end (see docs/PROTOCOL.md's <c>mcp</c> method,
    /// [issue #33](https://github.com/croicu/desk-tools/issues/33)) as a real, separate child
    /// process -- unlike <see cref="Ping_ThenShutdown_AgainstAManuallyStartedService"/>'s in-process
    /// <see cref="Program.Start"/> calls, this genuinely needs its own real stdin/stdout: once
    /// <c>desk mcp hello</c> starts proxying, its stdout carries only the launched tool's own MCP
    /// traffic, which this test both drives (writes a real <c>initialize</c> request to the child's
    /// stdin) and reads back (a real response), exactly as an external MCP client would. Windows-only
    /// (the underlying <c>DuplicateHandle</c> handoff is) -- guarded at runtime with
    /// <see cref="Assert.Inconclusive"/> rather than a compile-time <c>Platform/</c> split, same
    /// reasoning as <c>tests/Service/Integration/HelloTests.cs</c>'s own <c>mcp</c> test. win-x64 is
    /// src/Directory.Build.props' own default now, so this passes on a plain `dotnet build` --
    /// the guard only actually fires if someone deliberately overrides to a different RID.
    /// </summary>
    [TestMethod]
    public void Mcp_LaunchesRealHelloEntry_ProxiesARealInitializeExchange()
    {
        var outDir = ResolveOutDir();
        if (!string.Equals(new DirectoryInfo(outDir).Name, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"This test needs the real Windows HandleDuplicator -- win-x64 is src/Directory.Build.props' own default, but this build resolved to '{outDir}'. See issue #33/#32.");
            return;
        }

        var serviceDllPath = Path.Combine(outDir, "Service.dll");
        var deskDllPath = Path.Combine(outDir, "Desk.dll");
        var registryPath = Path.Combine(outDir, "mcp-registry", "hello.json");
        Assert.IsTrue(File.Exists(serviceDllPath), $"'{serviceDllPath}' not found.");
        Assert.IsTrue(File.Exists(deskDllPath), $"'{deskDllPath}' not found.");
        Assert.IsTrue(File.Exists(registryPath), $"'{registryPath}' not found -- build Hello too.");

        var serviceStartInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = outDir, UseShellExecute = false };
        serviceStartInfo.ArgumentList.Add(serviceDllPath);

        using var service = Process.Start(serviceStartInfo);
        Assert.IsNotNull(service, "Failed to start Service.");

        try
        {
            WaitUntilReachable();

            var mcpStartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = outDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                // No BOM on the write side -- same reasoning as Host.WriteEncoding/Client.WriteEncoding.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
            };
            mcpStartInfo.ArgumentList.Add(deskDllPath);
            mcpStartInfo.ArgumentList.Add("mcp");
            mcpStartInfo.ArgumentList.Add("hello");

            using var desk = Process.Start(mcpStartInfo);
            Assert.IsNotNull(desk, "Failed to start 'desk mcp hello'.");

            try
            {
                desk.StandardInput.NewLine = "\n";
                desk.StandardInput.WriteLine("""{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}""");
                desk.StandardInput.Flush();

                var response = desk.StandardOutput.ReadLine();
                Assert.IsFalse(string.IsNullOrWhiteSpace(response), "expected a proxied response line from 'desk mcp hello'.");

                using var doc = JsonDocument.Parse(response!);
                var root = doc.RootElement;
                Assert.AreEqual(1, root.GetProperty("id").GetInt32());
                Assert.AreEqual("2025-06-18", root.GetProperty("result").GetProperty("protocolVersion").GetString());
                Assert.AreEqual("hello", root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

                desk.StandardInput.Close();
                Assert.IsTrue(desk.WaitForExit((int)ExitWait.TotalMilliseconds), "'desk mcp hello' did not exit within the bounded wait after stdin closed.");
                Assert.AreEqual(0, desk.ExitCode);
            }
            finally
            {
                if (!desk.HasExited)
                {
                    desk.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            if (!service.HasExited)
            {
                service.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Polls with a real <c>ping</c> (not just a raw TCP connect) until Service is actually up and
    /// responding, or fails the test once <see cref="StartupWait"/> elapses -- Service's own
    /// process-start latency is otherwise a flaky race against the first ping.
    /// </summary>
    private static void WaitUntilReachable()
    {
        var deadline = DateTime.UtcNow + StartupWait;
        while (DateTime.UtcNow < deadline)
        {
            if (Program.Start(["ping"]) == 0)
            {
                return;
            }

            Thread.Sleep(200);
        }

        Assert.Fail("Service never became reachable within the startup wait.");
    }

    /// <summary>
    /// Mirrors this test assembly's own <c>net10.0[/&lt;RID&gt;]</c> structure onto
    /// <c>out/&lt;Configuration&gt;/net10.0/</c>, since src/Directory.Build.props and
    /// tests/Directory.Build.props apply the identical default <c>RuntimeIdentifier</c> (win-x64
    /// today -- see src/Directory.Build.props' own remarks) -- whatever RID segment (if any) this
    /// test itself landed under is exactly what Service.csproj/Desk.csproj's own build landed under
    /// too, so this stays correct if that default ever changes or gets overridden (e.g.
    /// `dotnet test -r linux-x64`), rather than hardcoding "win-x64" as a literal path segment.
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

    /// <summary>
    /// Walks up from the test assembly's own directory looking for Service.slnx (a repo-root
    /// marker) rather than hardcoding a fixed number of parent hops -- robust to the exact bin/obj
    /// folder depth without needing to keep this in sync if that ever changes.
    /// </summary>
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
