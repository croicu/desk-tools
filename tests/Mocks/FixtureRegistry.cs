using System.Text.Json;

namespace Croicu.Desk.Tools.Mocks;

/// <summary>
/// Writes a throwaway MCP tool registry for an integration test (issue #86): <c>&lt;dir&gt;/mcp-registry/&lt;name&gt;.json</c>
/// launching <c>command args</c>, and returns <c>&lt;dir&gt;</c> -- what <c>McpToolLauncher.Launch</c>'s
/// <c>registryBaseDirectory</c> (and <c>Host</c>'s <c>mcpRegistryBaseDirectory</c>) take. The test fixtures under
/// <c>tests/MCP</c> (the hello and goodbye tools) are not part of the product, so they have no generated registry
/// fragment in the build output; a test that launches one points a registry like this at its built files instead.
/// The directory is under the temp folder and is not removed (a leftover temp directory is harmless).
/// </summary>
public static class FixtureRegistry
{
    public static string Create(string name, string command, params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fixture-registry-{Guid.NewGuid():N}");
        var registryDir = Path.Combine(dir, "mcp-registry");
        Directory.CreateDirectory(registryDir);

        var fragment = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "stdio",
            ["command"] = command,
            ["args"] = args,
            ["env"] = new Dictionary<string, string>(),
        };
        File.WriteAllText(Path.Combine(registryDir, name + ".json"), JsonSerializer.Serialize(fragment));
        return dir;
    }
}
