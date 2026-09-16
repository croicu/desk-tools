using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Launches a registered MCP tool (see docs/PROTOCOL.md's MCP tool registry, issue #29) by name.
/// Reads <c>mcp-registry/&lt;name&gt;.json</c> from Service's own directory
/// (<see cref="AppContext.BaseDirectory"/>) -- the registry and every tool it references already
/// land in that same shared folder (<c>src/Directory.Build.props</c>' <c>BaseOutputPath</c> in dev,
/// or the MSI's <c>INSTALLFOLDER</c> once installed, see <c>installer/Setup.wixproj</c>'s
/// <c>PublishAppForSetup</c>) -- parses its full <c>.mcp.json</c>-shaped fragment, and delegates
/// the actual process spawn to <see cref="ToolLauncher"/> (a generic primitive with no registry
/// knowledge of its own). Just the process+pipes primitive (issue #30): not yet wired to any
/// external trigger (no echo/wire-protocol command, no actual MCP JSON-RPC proxying through a
/// client connection); a reusable capability for Service's own code to call.
/// </summary>
internal static class McpToolLauncher
{
    private const string RegistryDirName = "mcp-registry";

    /// <summary>
    /// Reads <paramref name="name"/>'s registry fragment and spawns the process it describes via
    /// <see cref="ToolLauncher"/>. Throws <see cref="AppError"/> if the registry entry doesn't
    /// exist, is malformed, or the process fails to start.
    /// <paramref name="registryBaseDirectory"/> is a testing seam (defaults to
    /// <see cref="AppContext.BaseDirectory"/>, Service's own real behavior) -- lets
    /// <c>tests/Service/Integration/HelloTests.cs</c> point this at the real, build-generated
    /// <c>out/&lt;Configuration&gt;/net10.0/</c> folder instead of the test assembly's own output
    /// directory, since that's where the real <c>hello</c> registry entry (and Hello.dll itself)
    /// actually land.
    /// </summary>
    internal static ToolProcess Launch(string name, string? registryBaseDirectory = null)
    {
        var baseDirectory = registryBaseDirectory ?? AppContext.BaseDirectory;
        var registryPath = Path.Combine(baseDirectory, RegistryDirName, $"{name}.json");
        if (!File.Exists(registryPath))
        {
            throw new AppError($"no registered MCP tool named '{name}' (expected '{registryPath}').");
        }

        McpToolFragment fragment;
        try
        {
            fragment = ParseFragment(File.ReadAllText(registryPath));
        }
        catch (JsonException error)
        {
            throw new AppError($"registry entry '{registryPath}' is not valid JSON: {error.Message}");
        }

        // args are relative to baseDirectory (see docs/PROTOCOL.md's registry schema), so that's
        // also the correct working directory for the spawned process to resolve them against --
        // not AppContext.BaseDirectory, which only coincides with baseDirectory in the real,
        // non-test-seam case.
        return ToolLauncher.Launch(fragment.Command, fragment.Args, fragment.Env, baseDirectory);
    }

    private static McpToolFragment ParseFragment(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("command", out var commandEl) || commandEl.ValueKind != JsonValueKind.String)
        {
            throw new AppError("registry entry is missing a string 'command' field.");
        }

        var command = commandEl.GetString() ?? throw new AppError("registry entry's 'command' field is null.");

        var args = new List<string>();
        if (root.TryGetProperty("args", out var argsEl))
        {
            if (argsEl.ValueKind != JsonValueKind.Array)
            {
                throw new AppError("registry entry's 'args' field must be an array.");
            }

            foreach (var item in argsEl.EnumerateArray())
            {
                args.Add(item.GetString() ?? string.Empty);
            }
        }

        var env = new Dictionary<string, string>();
        if (root.TryGetProperty("env", out var envEl))
        {
            if (envEl.ValueKind != JsonValueKind.Object)
            {
                throw new AppError("registry entry's 'env' field must be an object.");
            }

            foreach (var prop in envEl.EnumerateObject())
            {
                env[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return new McpToolFragment(command, args, env);
    }

    private sealed record McpToolFragment(string Command, List<string> Args, Dictionary<string, string> Env);
}
