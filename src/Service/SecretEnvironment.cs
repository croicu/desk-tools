using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Secrets;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Turns a tool's declared secrets (registry <c>"secrets"</c>, see <see cref="McpToolLauncher"/>) into
/// the environment variables it is launched with, by asking an <see cref="ISecretBroker"/> (issue #50,
/// slice 4b). The broker may show the master-password dialog and block until the user answers
/// (issue #66), so a launch waits for its secrets rather than starting without them.
///
/// <b>Never fails the launch.</b> Service is a convenience, never a gate (docs/ARCHITECTURE.md's MCP
/// tool invariants): any failure -- no broker, no KeePassXC, a cancelled dialog, a missing entry or key
/// -- costs only the variables it affects. The tool still starts, without them, and a warning names the
/// tool, the entry and the variables (never a value). Declarations are read one entry at a time, so a
/// bad entry does not take down the others.
/// </summary>
internal static class SecretEnvironment
{
    private const string Category = "secrets";

    /// <summary>
    /// The variables to add to <paramref name="toolName"/>'s environment: <c>env name -> value</c>,
    /// only for the declarations that could be resolved. Empty when there are none or no
    /// <paramref name="broker"/>.
    /// </summary>
    internal static Dictionary<string, string> Resolve(string toolName, IReadOnlyList<McpToolLauncher.McpSecretDeclaration> declarations, ISecretBroker? broker)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (declarations.Count == 0)
        {
            return resolved;
        }

        if (broker is null)
        {
            Logger.Warning($"mcp secrets: '{toolName}' declares {declarations.Count} secret(s) but no secret broker is configured; launching it without them.", Category);
            return resolved;
        }

        var byEntry = new Dictionary<string, List<McpToolLauncher.McpSecretDeclaration>>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (!byEntry.TryGetValue(declaration.Entry, out var group))
            {
                group = [];
                byEntry[declaration.Entry] = group;
            }

            group.Add(declaration);
        }

        foreach (var pair in byEntry)
        {
            ResolveEntry(toolName, pair.Key, pair.Value, broker, resolved);
        }

        Logger.Info($"mcp secrets: '{toolName}': {resolved.Count} of {declarations.Count} secret(s) resolved.", Category);
        return resolved;
    }

    private static void ResolveEntry(
        string toolName,
        string entry,
        List<McpToolLauncher.McpSecretDeclaration> group,
        ISecretBroker broker,
        Dictionary<string, string> resolved)
    {
        // One read per entry, each key once even if two variables name the same one.
        var keys = new List<string>();
        foreach (var declaration in group)
        {
            if (!keys.Contains(declaration.Key))
            {
                keys.Add(declaration.Key);
            }
        }

        IReadOnlyDictionary<string, string> values;
        try
        {
            Logger.Diagnostic($"mcp secrets: '{toolName}': reading {keys.Count} key(s) from entry '{entry}'.", Category);
            values = broker.Read(entry, keys);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An AppError has already logged its own detailed message; this says what it cost.
            Logger.Warning($"mcp secrets: '{toolName}': could not read entry '{entry}' ({error.Message}); launching it without {Names(group)}.", Category);
            return;
        }

        foreach (var declaration in group)
        {
            if (values.TryGetValue(declaration.Key, out var value))
            {
                resolved[declaration.Env] = value;
            }
            else
            {
                Logger.Warning($"mcp secrets: '{toolName}': entry '{entry}' returned no value for key '{declaration.Key}'; launching it without {declaration.Env}.", Category);
            }
        }
    }

    private static string Names(List<McpToolLauncher.McpSecretDeclaration> group)
    {
        var names = new List<string>();
        foreach (var declaration in group)
        {
            names.Add(declaration.Env);
        }

        return string.Join(", ", names);
    }
}
