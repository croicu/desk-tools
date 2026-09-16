using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Exercises <see cref="McpToolLauncher"/>'s registry-reading/parsing and its delegation to
/// <see cref="ToolLauncher"/>, entirely offline (real local file I/O and, for the "valid fragment"
/// case, a real local process spawn -- <c>dotnet</c> itself, already a hard dependency of running
/// `dotnet test` at all -- not a network call or another one of this repo's own apps; the latter,
/// launching the real <c>hello</c> registry entry, is
/// <c>tests/Service/Integration/HelloTests.cs</c>'s job instead, per CLAUDE.md's Architecture
/// convention 4). Each test gets its own throwaway directory (via
/// <see cref="McpToolLauncher.Launch"/>'s <c>registryBaseDirectory</c> testing seam), a fresh
/// temp path per test rather than a directory shared across this class's own tests -- MSTestSettings
/// parallelizes at method level (see HostTests' own remarks), so a shared directory raced deletes
/// against sibling tests still writing into it.
/// </summary>
[TestClass]
public sealed class McpToolLauncherTests
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"mcp-tool-launcher-tests-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    [TestMethod]
    public void Launch_UnknownName_ThrowsAppError()
    {
        var name = "no-such-tool";

        var error = Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
        StringAssert.Contains(error.Message, name);
    }

    [TestMethod]
    public void Launch_MalformedJson_ThrowsAppError()
    {
        var name = WriteRegistryEntry("{not valid json");

        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
    }

    [TestMethod]
    public void Launch_MissingCommandField_ThrowsAppError()
    {
        var name = WriteRegistryEntry("""{"name": "x"}""");

        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
    }

    [TestMethod]
    public void Launch_ArgsNotAnArray_ThrowsAppError()
    {
        var name = WriteRegistryEntry("""{"command": "dotnet", "args": "not-an-array"}""");

        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
    }

    [TestMethod]
    public void Launch_EnvNotAnObject_ThrowsAppError()
    {
        var name = WriteRegistryEntry("""{"command": "dotnet", "env": ["not-an-object"]}""");

        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
    }

    [TestMethod]
    public void Launch_UnresolvableCommand_ThrowsAppError()
    {
        var name = WriteRegistryEntry("""{"command": "definitely-not-a-real-command-xyz"}""");

        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.Launch(name, _baseDir));
    }

    [TestMethod]
    public void Launch_ValidFragment_StartsProcessWithRedirectedStdio()
    {
        var name = WriteRegistryEntry("""{"command": "dotnet", "args": ["--version"], "env": {}}""");

        using var tool = McpToolLauncher.Launch(name, _baseDir);

        var output = tool.StandardOutput.ReadLine();
        Assert.IsFalse(string.IsNullOrWhiteSpace(output), "expected 'dotnet --version' to write a version string to stdout.");
    }

    private string WriteRegistryEntry(string json)
    {
        const string name = "test-tool";
        var registryDir = Path.Combine(_baseDir, "mcp-registry");
        Directory.CreateDirectory(registryDir);
        File.WriteAllText(Path.Combine(registryDir, $"{name}.json"), json);
        return name;
    }
}
