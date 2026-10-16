using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Decides whether a launched tool gets a visible console window (issue #44). Hidden by default for every tool,
/// whatever its flavor (a .NET dll, a Python script) and whichever build: the tools talk over redirected stdio and
/// never need a console, and a window (and its <c>conhost.exe</c>) per tool is only noise. Two settings control it:
/// <list type="bullet">
/// <item>the core <c>settings.console</c> (boolean, default false) -- the default for every tool;</item>
/// <item>an optional <c>"tools"</c> section overriding it per tool:
/// <c>"tools": { "hello": { "console": true } }</c>. Read with <see cref="Settings.Section"/> on every launch, so
/// it can live in settings.json, the per-user file or settings.local.json, and a change applies to the next launch
/// without restarting Service. (Showing a console is harmless, so the per-user tier is fine for it.)</item>
/// </list>
/// A malformed override is a warning, never a failed launch (docs/ARCHITECTURE.md, "Invariants for MCP tools"): the
/// tool is launched with the default.
/// </summary>
internal static class ToolConsole
{
    private const string Category = "tools";
    private const string SectionName = "tools";
    private const string ConsoleKey = "console";

    /// <summary>
    /// Whether <paramref name="toolName"/> should get a visible console. <paramref name="readToolsSection"/> is a
    /// testing seam (defaults to reading the real <c>"tools"</c> section of the settings files).
    /// </summary>
    internal static bool IsVisible(string toolName, ISettingsProvider settings, Func<Dictionary<string, JsonElement>>? readToolsSection = null)
    {
        var visible = settings.ToolConsole;

        Dictionary<string, JsonElement> tools;
        try
        {
            tools = (readToolsSection ?? ReadToolsSection)();
        }
        catch (SettingsError error)
        {
            Logger.Warning($"tools: the '{SectionName}' settings could not be read ({error.Message}); using the default console setting for '{toolName}'.", Category);
            return visible;
        }

        foreach (var pair in tools)
        {
            if (!string.Equals(pair.Key, toolName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value.ValueKind != JsonValueKind.Object)
            {
                Logger.Warning($"tools: '{SectionName}.{pair.Key}' must be an object like {{ \"console\": true }}; using the default console setting.", Category);
                return visible;
            }

            if (!pair.Value.TryGetProperty(ConsoleKey, out var consoleElement))
            {
                return visible;
            }

            if (consoleElement.ValueKind != JsonValueKind.True && consoleElement.ValueKind != JsonValueKind.False)
            {
                Logger.Warning($"tools: '{SectionName}.{pair.Key}.{ConsoleKey}' must be true or false; using the default console setting.", Category);
                return visible;
            }

            return consoleElement.GetBoolean();
        }

        return visible;
    }

    private static Dictionary<string, JsonElement> ReadToolsSection()
    {
        return Settings.Section(SectionName);
    }
}
