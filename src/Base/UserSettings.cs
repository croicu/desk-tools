using System.Text.Json;
using System.Text.Json.Nodes;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// The per-user settings file, <c>%LOCALAPPDATA%\Desk Tools\settings.json</c> (the platform's local
/// application-data folder elsewhere): a writable tier that <see cref="Settings.Section"/> merges
/// between the working-directory <c>settings.json</c> and <c>settings.local.json</c>. It exists for
/// values only the user's own machine can know and that the program discovers at run time -- the
/// first one is the KeePass database path the file picker records (issue #50, section
/// <c>secrets</c>, key <c>databasePath</c>).
///
/// <b>Trust boundary.</b> Any process running as the user can write this file, while Service reads it
/// elevated. So it is honored <i>only for non-core sections</i> (siblings of the <c>"settings"</c>
/// object), never for core settings like <c>logDir</c>, <c>port</c> or <c>debug</c>: a value here must
/// never be able to steer what the elevated process does beyond the one feature that reads it, and
/// that feature validates its own value. <see cref="Set"/> refuses the core section, and
/// <see cref="Settings.Section"/> ignores it from this tier, so the rule holds from both sides.
///
/// Cross-platform (Architecture convention 11): uses <see cref="Environment.SpecialFolder"/> and
/// <see cref="Path.Combine(string, string)"/> only.
/// </summary>
public static class UserSettings
{
    /// <summary>The core settings object's name -- never honored from, or writable to, this tier.</summary>
    internal const string CoreSection = "settings";

    private const string Category = DiagnosticsCategories.Settings;

    // Moving the temp file over the real one can fail transiently on Windows (ACCESS_DENIED / a
    // sharing violation) while another process -- antivirus, the search indexer, a cloud-sync client --
    // briefly has the destination open. That is not a real failure of a settings write, so retry for
    // about half a second before giving up. Found by a test that intermittently failed with
    // UnauthorizedAccessException.
    private const int MoveAttempts = 10;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// The default per-user file, or null if the platform reports no local application-data folder
    /// (rare; e.g. no home directory). Null, not a relative path: a relative fallback would silently
    /// read and write whatever the working directory happens to be.
    /// </summary>
    public static string? DefaultPath { get; } = ComputeDefaultPath();

    /// <summary>
    /// Sets <c>section.key</c> to <paramref name="value"/> in the per-user file (<paramref name="path"/>
    /// defaults to <see cref="DefaultPath"/>), creating the folder and file if needed and keeping every
    /// other section and key as they were. Written to a temporary file and moved into place, so a
    /// reader never sees a half-written file; concurrent writers are last-one-wins. Throws
    /// <see cref="SettingsError"/> -- and leaves the file untouched -- for the core section, a
    /// non-object section, or an existing file that isn't valid JSON (it is not overwritten: the user
    /// should see and fix it rather than lose it).
    /// </summary>
    public static void Set(string section, string key, string value, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key))
        {
            throw new SettingsError("a per-user setting needs a non-empty section and key.");
        }

        if (string.Equals(section, CoreSection, StringComparison.Ordinal))
        {
            throw new SettingsError($"'{CoreSection}' is the core settings object and is not honored from the per-user file; use another section name.");
        }

        var filePath = path ?? DefaultPath
            ?? throw new SettingsError("cannot locate the per-user settings file: this platform has no local application-data folder.");

        var root = ReadRoot(filePath);

        JsonObject target;
        if (root.TryGetPropertyValue(section, out var existing) && existing is not null)
        {
            target = existing as JsonObject
                ?? throw new SettingsError($"'{section}' in '{filePath}' must be a JSON object; fix or delete it.");
        }
        else
        {
            target = new JsonObject();
            root[section] = target;
        }

        target[key] = value;

        Write(filePath, root);
        Logger.Info($"per-user settings: set '{section}.{key}' in '{filePath}'.", Category);
    }

    private static JsonObject ReadRoot(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject
                ?? throw new SettingsError($"'{filePath}' must contain a JSON object; fix or delete it.");
        }
        catch (JsonException error)
        {
            throw new SettingsError($"'{filePath}' is not valid JSON ({error.Message}); fix or delete it.");
        }
    }

    private static void Write(string filePath, JsonObject root)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(WriteOptions));
            MoveIntoPlace(temporaryPath, filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void MoveIntoPlace(string temporaryPath, string filePath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, filePath, overwrite: true);
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                Logger.Diagnostic($"per-user settings: '{filePath}' is busy (attempt {attempt} of {MoveAttempts}): {error.Message}", Category);
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    private static string? ComputeDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Desk Tools", "settings.json");
    }
}
