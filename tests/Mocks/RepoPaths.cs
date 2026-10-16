namespace Croicu.Desk.Tools.Mocks;

/// <summary>
/// Resolves <c>out/&lt;Configuration&gt;/net10.0/[&lt;RID&gt;]/</c> -- the shared build output
/// folder every <c>src/</c> app builds into (see <c>src/Directory.Build.props</c>' own
/// <c>BaseOutputPath</c>) -- by mirroring the calling test assembly's own
/// <c>net10.0[/&lt;RID&gt;]</c> structure, since <c>src/Directory.Build.props</c> and
/// <c>tests/Directory.Build.props</c> apply the identical default <c>RuntimeIdentifier</c>
/// (win-x64 today) -- whatever RID segment (if any) the calling test itself landed under is
/// exactly what the app it's driving landed under too, so this stays correct if that default
/// ever changes or gets overridden (e.g. <c>DESK_TOOLS_RID=linux-x64</c>), rather than hardcoding
/// "win-x64" as a literal path segment. Shared by every cross-project Integration test that needs
/// to find a real build artifact (<c>Service.dll</c>, <c>Desk.dll</c>, <c>mcp-registry/*.json</c>)
/// rather than each duplicating this walk-up logic -- extracted here once a third call site
/// (<c>tests/Service/Integration/GoodbyeTests.cs</c>) made the existing two-file duplication
/// (<c>tests/Desk/Integration/ServiceTests.cs</c>, <c>tests/Service/Integration/HelloTests.cs</c>)
/// worth resolving, per CLAUDE.md's Coding Style "wait for real duplication" guidance.
/// </summary>
public static class RepoPaths
{
    public static string ResolveOutDir()
    {
        var (configuration, ridSegment) = ResolveConfigurationAndRid();
        var outDir = Path.Combine(FindRepoRoot(), "out", configuration, "net10.0");
        return ridSegment is null ? outDir : Path.Combine(outDir, ridSegment);
    }

    /// <summary>
    /// The build output folder of a test fixture tool under <c>tests/MCP/&lt;fixture&gt;/</c> (issue #86: the hello
    /// tool), <c>tests/MCP/&lt;fixture&gt;/bin/&lt;Configuration&gt;/net10.0/[&lt;RID&gt;]/</c> -- mirroring the calling test
    /// assembly's own configuration and RID exactly as <see cref="ResolveOutDir"/> does, since
    /// <c>tests/Directory.Build.props</c> gives every project there the same defaults.
    /// </summary>
    public static string ResolveFixtureOutDir(string fixture)
    {
        var (configuration, ridSegment) = ResolveConfigurationAndRid();
        var dir = Path.Combine(FindRepoRoot(), "tests", "MCP", fixture, "bin", configuration, "net10.0");
        return ridSegment is null ? dir : Path.Combine(dir, ridSegment);
    }

    private static (string Configuration, string? RidSegment) ResolveConfigurationAndRid()
    {
        var testDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? ridSegment = null;
        if (!string.Equals(testDir.Name, "net10.0", StringComparison.OrdinalIgnoreCase))
        {
            ridSegment = testDir.Name;
            testDir = testDir.Parent!;
        }

        return (FindConfigurationFolder(testDir), ridSegment);
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
    public static string FindRepoRoot()
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
