using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// <c>secrets.rememberPassword</c> (boolean, default <b>true</b>): whether the master password is remembered across
/// Service runs (issue #84). <c>false</c> turns the feature off entirely -- nothing is read from or written to disk.
/// Read with <see cref="Settings.Section"/> once, when the broker is created (so, like every setting, a change takes
/// effect when Service restarts). Unreadable or malformed means the default, with a warning: it must never stop a launch.
/// </summary>
internal static class RememberSetting
{
    private const string SectionName = "secrets";
    private const string KeyName = "rememberPassword";

    internal static bool IsEnabled(Func<Dictionary<string, JsonElement>>? readSection = null)
    {
        Dictionary<string, JsonElement> section;
        try
        {
            section = (readSection ?? ReadSection)();
        }
        catch (SettingsError error)
        {
            Logger.Warning($"secrets: the '{SectionName}' settings could not be read ({error.Message}); remembering the master password stays on.", LogCategory.Secrets);
            return true;
        }

        if (!section.TryGetValue(KeyName, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            Logger.Warning($"secrets: '{SectionName}.{KeyName}' must be true or false; remembering the master password stays on.", LogCategory.Secrets);
            return true;
        }

        return value.GetBoolean();
    }

    private static Dictionary<string, JsonElement> ReadSection()
    {
        return Settings.Section(SectionName);
    }
}
