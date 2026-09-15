using System.Diagnostics;

namespace Croicu.Desk.Tools.Desk.Tests.Integration;

/// <summary>
/// Exercises the real Desk CLI (<see cref="Program.Start"/>) against a real <c>Service</c> instance
/// this test starts itself, by launching <c>out/&lt;Configuration&gt;/net10.0/Service.dll</c> as a
/// plain child process -- no shared test-framework setup/fixture (no
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
/// ProjectReference just to resolve a path).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class ServiceTests
{
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Ping_ThenShutdown_AgainstAManuallyStartedService()
    {
        var serviceDllPath = ResolveServiceDllPath();
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

    private static string ResolveServiceDllPath()
    {
        // tests/Desk/bin/<Configuration>/net10.0/ and out/<Configuration>/net10.0/Service.dll share
        // the same <Configuration> segment and repo root -- see src/Directory.Build.props' shared
        // BaseOutputPath.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(FindRepoRoot(), "out", configuration, "net10.0", "Service.dll");
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
