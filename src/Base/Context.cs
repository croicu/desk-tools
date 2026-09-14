using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Wires <see cref="Settings"/> (settings.json) into <see cref="Logger"/> as one step, and combines
/// <see cref="Settings.Debug"/> with a caller-supplied CLI override into the single resolved
/// <see cref="Debug"/> value a host's Program.cs actually acts on -- so Program.cs doesn't have to
/// remember any of this wiring itself; constructing this via <see cref="Create"/> is the wiring:
///
/// - Applies <see cref="Settings.LogLevel"/>/<see cref="Settings.LogCategories"/>/
///   <see cref="Settings.ExcludedCategories"/> to the console sink (<see cref="Logger.ConfigureConsole"/>).
/// - Installs a <see cref="DebugLog"/> alongside the console sink (see Logger.cs's multi-sink
///   fan-out) when the resolved <see cref="Debug"/> is true.
/// - Installs a <see cref="FileLog"/> alongside whatever else is active when a log directory is
///   resolved (<paramref name="logDirOverride"/> -- see <see cref="Create"/> -- taking precedence
///   over <see cref="Settings.LogDir"/> if both are given, same precedence direction as
///   <paramref name="debugOverride"/> over <see cref="Settings.Debug"/>).
///
/// Deliberately not <see cref="Settings"/>'s job: <see cref="Settings.Load"/> is a pure settings.json
/// parse with no side effects of its own (safe to call repeatedly, e.g. from tests), and wiring up
/// Logger is a host-level policy decision, not a config-parsing one.
///
/// Takes plain <paramref name="debugOverride"/>/<paramref name="logDirOverride"/> values rather than
/// a host's own CLI-arguments type (e.g. Service's <c>CliArguments</c>) -- Architecture convention 9
/// keeps <c>src/Base</c> from ever referencing a consuming app's own namespace, so this stays
/// reusable by any host's Program.cs, not coupled to one app's flag-parsing shape.
///
/// Exposed via <see cref="Current"/>, AsyncLocal-scoped like <see cref="Settings.Current"/> --
/// Architecture convention 10 -- so a caller deep in the call stack can read the resolved
/// <see cref="Debug"/> flag without it being threaded through every method signature, and so a host
/// serving multiple heterogeneous clients from one process keeps each client's own resolved context
/// independent.
/// </summary>
public sealed class Context
{
    private static readonly AsyncLocal<Context?> CurrentInstance = new();

    public bool Debug { get; }

    private Context(bool debug)
    {
        Debug = debug;
    }

    public static Context Current => CurrentInstance.Value ?? throw new InvalidOperationException("Context.Create() must be called first.");

    public static Context Create(ISettingsProvider settings, bool debugOverride = false, string? logDirOverride = null)
    {
        Logger.ConfigureConsole(
            minLevel: settings.LogLevel,
            categories: settings.LogCategories,
            excludedCategories: settings.ExcludedCategories);

        var debug = settings.Debug || debugOverride;
        if (debug)
        {
            Logger.SetLogger(DebugLog.Create());
        }

        var logDir = logDirOverride ?? settings.LogDir;
        if (logDir is not null)
        {
            Logger.SetLogger(FileLog.Create(logDir));
        }

        var context = new Context(debug);
        CurrentInstance.Value = context;

        return context;
    }

    /// <summary>
    /// The full bootstrap ceremony a host's Program.cs otherwise has to hand-roll: brackets
    /// <paramref name="console"/>'s lifecycle around everything (so it's released even if settings
    /// fail to load or <paramref name="run"/> throws), loads <see cref="Settings"/> (translating a
    /// malformed settings.json into a logged error and exit code 1, same as a caller who used to do
    /// this by hand), wires it into <see cref="Logger"/> via <see cref="Create"/>, then invokes
    /// <paramref name="run"/> -- catching <see cref="AppError"/> the same way <see cref="Create"/>'s
    /// resolved <see cref="Debug"/> already implies: rethrow (unhandled, full stack trace) when
    /// debug is on, otherwise a logged error and exit code 1.
    ///
    /// <paramref name="appName"/> prefixes those two error-logging paths (e.g. "desk-tools: error:
    /// ...") -- a required, explicit parameter rather than something <c>src/Base</c> could hardcode,
    /// since only the host knows its own name.
    ///
    /// <paramref name="run"/> reads <see cref="Settings.Current"/>/<see cref="Current"/> for
    /// whatever it needs rather than taking them as parameters -- both are already resolved (via
    /// <see cref="Settings.Load"/>/<see cref="Create"/>) by the time this calls it.
    /// </summary>
    public static int Start(IConsole console, string appName, string? settingsPath, bool debugOverride, string? logDirOverride, Func<int> run)
    {
        console.EnsureConsole();
        try
        {
            Settings settings;
            try
            {
                settings = settingsPath is null ? Settings.Load() : Settings.Load(path: settingsPath);
            }
            catch (AppError error)
            {
                Logger.Error($"{appName}: error: {error.Message}");
                return 1;
            }

            var context = Create(settings, debugOverride, logDirOverride);

            try
            {
                return run();
            }
            catch (AppError error)
            {
                if (context.Debug)
                {
                    throw;
                }

                Logger.Error($"{appName}: error: {error.Message}");
                return 1;
            }
        }
        finally
        {
            console.ReleaseConsole();
        }
    }
}
