using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Mocks;

/// <summary>
/// In-memory <see cref="ISettingsProvider"/> fake for tests -- no file I/O, unlike the real
/// <see cref="Settings"/> (which only ever reads from settings.json/settings.local.json).
/// Construct directly with object-initializer syntax; every property defaults to what
/// <see cref="Settings.Load"/> itself returns when no settings.json is present, so a test that
/// doesn't care about a particular value doesn't have to set it.
/// </summary>
public sealed class TestSettings : ISettingsProvider
{
    public bool Debug { get; init; }

    public TelemetryLevel LogLevel { get; init; } = TelemetryLevel.Error;

    public List<string> LogCategories { get; init; } = new() { DiagnosticsCategories.General };

    public List<string> ExcludedCategories { get; init; } = new();
}
