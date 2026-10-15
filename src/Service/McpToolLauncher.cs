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
    private const string RegistryCategory = "registry";

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

    /// <summary>
    /// Parses one registry fragment. Internal (not private) so tests can feed it fixtures directly.
    /// Throws <see cref="AppError"/> -- with a message naming the offending field -- for anything
    /// malformed, including a malformed optional <c>secrets</c> array (see
    /// <see cref="ParseSecrets"/>).
    /// </summary>
    internal static McpToolFragment ParseFragment(string json)
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

        var secrets = ParseSecrets(root, env);

        return new McpToolFragment(command, args, env, secrets);
    }

    /// <summary>
    /// Reads the optional <c>secrets</c> array (issue #50, slice 4a): one object per secret a tool
    /// declares it needs, <c>{"env": "NTFY_TOPIC", "entry": "ntfy", "key": "desk-pager"}</c> -- the
    /// environment variable it will arrive in, the KeePass entry, and the key inside that entry's
    /// Notes. Missing means none. Only <i>declarations</i> are parsed here; nothing resolves a value
    /// yet (the next slice), so no secret is read or logged.
    ///
    /// <b>Never throws, never blocks the launch.</b> The manifest is optional and Service is a
    /// convenience, not a gate: a tool must be able to run without what a declaration would have
    /// given it, so a malformed declaration costs only <i>that</i> capability -- it is skipped with a
    /// warning naming it, the remaining declarations are kept, and the tool still starts. (A fragment
    /// with no usable <c>command</c> is a different matter: there is nothing to launch.) A declaration
    /// is skipped if its <c>env</c> is not a plain environment-variable name, repeats an earlier one
    /// (case-insensitively, as Windows environments are), is already set by the fragment's own
    /// <c>env</c> (the explicit value wins; two sources for one variable would be ambiguous), or is
    /// <see cref="ParentWatchdog.EnvironmentVariable"/>, which Service sets itself for every tool.
    /// </summary>
    private static List<McpSecretDeclaration> ParseSecrets(JsonElement root, Dictionary<string, string> env)
    {
        var secrets = new List<McpSecretDeclaration>();
        if (!root.TryGetProperty("secrets", out var secretsEl))
        {
            return secrets;
        }

        if (secretsEl.ValueKind != JsonValueKind.Array)
        {
            SkipSecret("registry entry's 'secrets' field must be an array; ignoring all secret declarations");
            return secrets;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in secretsEl.EnumerateArray())
        {
            var where = $"secrets[{index}]";
            index++;

            if (item.ValueKind != JsonValueKind.Object)
            {
                SkipSecret($"{where} must be an object with string 'env', 'entry' and 'key' fields");
                continue;
            }

            var envName = ReadString(item, "env");
            var entry = ReadString(item, "entry");
            var key = ReadString(item, "key");

            if (envName is null || entry is null || key is null)
            {
                SkipSecret($"{where} is missing a non-empty string 'env', 'entry' or 'key' field");
                continue;
            }

            if (!IsValidEnvironmentVariableName(envName))
            {
                SkipSecret($"{where} has an invalid environment variable name '{envName}' (use letters, digits and '_', not starting with a digit)");
                continue;
            }

            if (string.Equals(envName, ParentWatchdog.EnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            {
                SkipSecret($"{where} may not use '{envName}': Service sets that variable itself for every tool it launches");
                continue;
            }

            if (!seen.Add(envName))
            {
                SkipSecret($"{where} declares '{envName}' more than once");
                continue;
            }

            if (SetsVariable(env, envName))
            {
                SkipSecret($"{where} declares '{envName}', which the fragment's own 'env' already sets: a variable can have only one source");
                continue;
            }

            secrets.Add(new McpSecretDeclaration(envName, entry, key));
        }

        return secrets;
    }

    private static void SkipSecret(string reason)
    {
        // Logger.Warning directly, not an AppError (whose constructor logs too and which would be
        // thrown): the launch carries on without this declaration.
        Logger.Warning($"mcp registry: {reason}; launching the tool without it.", RegistryCategory);
    }

    private static bool SetsVariable(Dictionary<string, string> env, string name)
    {
        foreach (var existing in env.Keys)
        {
            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The field's value if it is a non-blank string, otherwise null.</summary>
    private static string? ReadString(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsValidEnvironmentVariableName(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A parsed registry fragment. <see cref="Secrets"/> is empty for a tool that declares none.</summary>
    internal sealed record McpToolFragment(string Command, List<string> Args, Dictionary<string, string> Env, List<McpSecretDeclaration> Secrets);

    /// <summary>
    /// One secret a tool declares: <see cref="Env"/> is the environment variable it arrives in,
    /// <see cref="Entry"/> the KeePass entry, <see cref="Key"/> the <c>key: value</c> line in that
    /// entry's Notes. Declarations only -- never a value.
    /// </summary>
    internal sealed record McpSecretDeclaration(string Env, string Entry, string Key);
}
