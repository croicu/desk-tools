using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Where the chosen KeePass database path lives: <c>secrets.databasePath</c> in the settings files
/// (read through <see cref="Settings.Section"/>, so every tier is merged), written to the per-user file
/// (<see cref="UserSettings.Set"/>, issue #58). The per-user tier is exactly the right home -- local to
/// this machine and user, writable by the program -- and is honored for this non-core section.
/// All four paths are testing seams, defaulting to the real locations.
/// </summary>
internal sealed class DatabasePathStore : IDatabasePathStore
{
    internal const string SectionName = "secrets";
    internal const string KeyName = "databasePath";

    private readonly string? _userPath;
    private readonly string? _path;
    private readonly string? _localPath;
    private readonly string? _modulePath;

    internal DatabasePathStore(string? userPath = null, string? path = null, string? localPath = null, string? modulePath = null)
    {
        _userPath = userPath;
        _path = path;
        _localPath = localPath;
        _modulePath = modulePath;
    }

    public string Description => $"'{SectionName}.{KeyName}' in the settings files (written to the per-user file '{_userPath ?? UserSettings.DefaultPath}')";

    public string? Get()
    {
        var section = Settings.Section(SectionName, _path, _localPath, _modulePath, _userPath);
        if (!section.TryGetValue(KeyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            Logger.Warning($"secrets: '{SectionName}.{KeyName}' is not a string; treating it as unset.", LogCategory.Secrets);
            return null;
        }

        return value.GetString();
    }

    public void Set(string path)
    {
        UserSettings.Set(SectionName, KeyName, path, _userPath);
    }
}
