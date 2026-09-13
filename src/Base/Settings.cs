using System.Text.Json;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Layered JSON settings, three tiers deep: the module's own directory's <c>settings.json</c> (the
/// base -- e.g. the file shipped alongside an installed build), then the working directory's own
/// <c>settings.json</c> overriding it key-by-key (a per-deployment/per-invocation override), then
/// the working directory's <c>settings.local.json</c> overriding that (a personal, gitignored
/// override file -- see .gitignore). Load once via <see cref="Load"/>, then read back via
/// <see cref="Current"/>. Implements <see cref="ISettingsProvider"/> so a caller that accepts that
/// interface works unmodified with a real, file-backed instance.
/// </summary>
public sealed record Settings : ISettingsProvider
{
    private const string DefaultSettingsPath = "./settings.json";
    private const string DefaultLocalPath = "./settings.local.json";

    // AppContext.BaseDirectory is fixed for the lifetime of the process, so this is computed once
    // rather than re-resolved on every Load()/Section() call.
    private static readonly string DefaultModulePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    // AsyncLocal rather than a plain static field: scopes "the current settings" to the logical
    // call context (flows across await continuations, doesn't leak into an unrelated concurrent
    // Load()/Current pair) -- same reasoning as ConsoleLogSink's InstanceActive in Diagnostics.cs.
    private static readonly AsyncLocal<Settings?> CurrentInstance = new();

    public bool Debug { get; }
    public TelemetryLevel LogLevel { get; }
    public List<string> LogCategories { get; }
    public List<string> ExcludedCategories { get; }
    public int IdleTimeout { get; }

    private Settings(
        bool debug,
        TelemetryLevel logging,
        List<string> logCategories,
        List<string> excludedCategories,
        int idleTimeout)
    {
        Debug = debug;
        LogLevel = logging;
        LogCategories = logCategories;
        ExcludedCategories = excludedCategories;
        IdleTimeout = idleTimeout;
    }

    public static Settings Current => CurrentInstance.Value ?? throw new InvalidOperationException("Settings.Load() must be called first.");

    public static Settings Load(string? path = null, string? localPath = null, string? modulePath = null)
    {
        var debug = false;
        var logLevel = TelemetryLevel.Error;
        var logCategories = new List<string>();
        var excludedCategories = new List<string>();
        var expandCategories = false;
        var idleTimeout = 600;

        var payload = LoadPayload(modulePath ?? DefaultModulePath, path ?? DefaultSettingsPath, localPath ?? DefaultLocalPath);

        if (payload.Count > 0)
        {
            if (payload.TryGetValue("debug", out var debugEl) && (debugEl.ValueKind == JsonValueKind.True || debugEl.ValueKind == JsonValueKind.False))
            {
                debug = debugEl.GetBoolean();
            }

            var logLevelText = "error";
            if (payload.TryGetValue("logLevel", out var logLevelEl) && logLevelEl.ValueKind == JsonValueKind.String)
            {
                logLevelText = logLevelEl.GetString() ?? "error";
            }

            if (!TryParseLevel(logLevelText, out logLevel))
            {
                throw new SettingsError("'settings.logLevel' in settings.json must be one of: verbose, info, warning, error, critical");
            }

            if (payload.ContainsKey("logLevel"))
            {
                // An explicit logLevel is more specific than the blanket debug flag, so it wins
                // outright whenever the two would otherwise disagree about how much to show:
                // setting a permissive level (e.g. "verbose") shouldn't be silently muted by
                // debug's own separate category default, and setting a restrictive level (e.g.
                // "critical") shouldn't be forced open just because debug=true. debug only falls
                // back into play here when logLevel was left at its implicit default.
                expandCategories = logLevel < TelemetryLevel.Error;
            }
            else
            {
                expandCategories = debug;
            }

            if (payload.TryGetValue("logCategories", out var logCategoriesEl))
            {
                if (logCategoriesEl.ValueKind != JsonValueKind.Array)
                {
                    throw new SettingsError("'settings.logCategories' in settings.json must be an array of strings.");
                }

                foreach (var item in logCategoriesEl.EnumerateArray())
                {
                    logCategories.Add(item.ToString());
                }
            }

            if (logCategories.Count > 0 && expandCategories && !logCategories.Contains(DiagnosticsCategories.General))
            {
                // Expanded-logging intent (see expandCategories above) always keeps General
                // alongside an explicit narrower list -- its baseline info should stay visible
                // even while zoomed into one category, not be silently dropped by naming a single
                // other category.
                logCategories.Insert(0, DiagnosticsCategories.General);
            }

            if (payload.TryGetValue("excludedCategories", out var excludedEl))
            {
                if (excludedEl.ValueKind != JsonValueKind.Array)
                {
                    throw new SettingsError("'settings.excludedCategories' in settings.json must be an array of strings.");
                }

                foreach (var item in excludedEl.EnumerateArray())
                {
                    excludedCategories.Add(item.ToString());
                }
            }

            if (payload.TryGetValue("idleTimeout", out var idleTimeoutEl))
            {
                if (idleTimeoutEl.ValueKind != JsonValueKind.Number)
                {
                    throw new SettingsError("'settings.idleTimeout' in settings.json must be a number.");
                }

                idleTimeout = idleTimeoutEl.GetInt32();
            }
        }

        if (logCategories.Count == 0)
        {
            // No explicit override: restricts console noise to General unless expanded logging
            // was signaled (an explicit permissive logLevel, or debug=true when logLevel was left
            // at its default -- see expandCategories above), in which case everything shows, same
            // as an empty filter always has.
            logCategories = expandCategories ? new List<string>() : new List<string> { DiagnosticsCategories.General };
        }

        var settings = new Settings(debug, logLevel, logCategories, excludedCategories, idleTimeout);
        CurrentInstance.Value = settings;

        return settings;
    }

    /// <summary>
    /// Returns one named top-level section of settings.json (merged across all three tiers -- see
    /// the class doc comment), e.g. a plugin's or subsystem's own config section living as a
    /// sibling of the core "settings" object, not nested inside it. settings.json's shape is
    /// uniform across consumer repos, so this is the public entry point for a caller outside this
    /// class to reach its own section without knowing how the file is read or merged -- that stays
    /// encapsulated in <see cref="LoadTopLevel"/> (distinct from <see cref="LoadPayload"/>, which
    /// only reads the "settings" object itself for <see cref="Load"/>'s own use).
    /// </summary>
    public static Dictionary<string, JsonElement> Section(string name, string? path = null, string? localPath = null, string? modulePath = null)
    {
        var topLevel = LoadTopLevel(modulePath ?? DefaultModulePath, path ?? DefaultSettingsPath, localPath ?? DefaultLocalPath);
        if (!topLevel.TryGetValue(name, out var sectionEl))
        {
            return new Dictionary<string, JsonElement>();
        }

        if (sectionEl.ValueKind != JsonValueKind.Object)
        {
            throw new SettingsError($"'{name}' in settings.json must be a JSON object.");
        }

        var result = new Dictionary<string, JsonElement>();
        foreach (var prop in sectionEl.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }

        return result;
    }

    /// <summary>
    /// Reads the module-dir and working-directory settings.json's "settings" objects (module first,
    /// working directory overriding it key-by-key), then the working directory's
    /// settings.local.json's own "settings" object overriding that. Private -- Section() is the
    /// public way for a caller outside this class to reach a section of the merged file.
    /// </summary>
    private static Dictionary<string, JsonElement> LoadPayload(string modulePath, string path, string localPath)
    {
        var payload = new Dictionary<string, JsonElement>();

        var moduleFound = MergeSettingsObject(payload, modulePath, "module");
        var pathFound = MergeSettingsObject(payload, path, "working directory");

        if (!moduleFound && !pathFound)
        {
            // Load()'s own consumer effect: with no settings.json at either base tier, every knob
            // it reads (debug, logLevel, logCategories, excludedCategories, idleTimeout)
            // falls back to its own restrictive default -- a meaningful behavior change (e.g.
            // Verbose/Info logging is unreachable for the rest of the run without one), so this is
            // Warning rather than Info.
            Logger.Warning($"no settings file found at module path '{modulePath}' or working directory '{path}'; falling back to restrictive defaults (debug=false, logLevel=error).", DiagnosticsCategories.Settings);
        }

        if (File.Exists(localPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("settings", out var localSettingsEl) &&
                localSettingsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in localSettingsEl.EnumerateObject())
                {
                    payload[prop.Name] = prop.Value.Clone();
                }

                Logger.Diagnostic($"local settings override found and parsed: '{localPath}'", DiagnosticsCategories.Settings);
            }
            else
            {
                // Unlike a malformed module/working-directory settings.json (which throws), a
                // malformed settings.local.json is silently ignored by design -- see the
                // caller-facing shape check above. Silent was the wrong call by itself (a typo'd
                // override would otherwise vanish with zero signal), so surface it as a Warning
                // instead of leaving it truly silent.
                Logger.Warning($"local settings file '{localPath}' exists but has no valid 'settings' object; ignoring it.", DiagnosticsCategories.Settings);
            }
        }
        else
        {
            // Absence of an optional, personal override file is the normal, expected case for most
            // instances -- not an unexpected condition, so Verbose rather than Info/Warning.
            Logger.Diagnostic($"no local settings override at '{localPath}'.", DiagnosticsCategories.Settings);
        }

        return payload;
    }

    /// <summary>
    /// Merges one settings.json-shaped file's "settings" object into <paramref name="payload"/>
    /// (key-by-key, overwriting anything already present under the same key). Shared by both the
    /// module-dir and working-directory tiers in <see cref="LoadPayload"/> -- unlike
    /// settings.local.json (handled separately there), both of these are "strict": a malformed file
    /// throws rather than being silently ignored, since neither is the personal/optional override
    /// settings.local.json is. Returns whether the file existed, so the caller can tell whether
    /// falling back to restrictive defaults is actually happening only once neither tier supplied
    /// anything.
    /// </summary>
    private static bool MergeSettingsObject(Dictionary<string, JsonElement> payload, string path, string tierName)
    {
        if (!File.Exists(path))
        {
            Logger.Diagnostic($"no {tierName} settings file at '{path}'.", DiagnosticsCategories.Settings);
            return false;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new SettingsError($"'{path}' must contain a JSON object.");
        }

        if (doc.RootElement.TryGetProperty("settings", out var settingsEl))
        {
            if (settingsEl.ValueKind != JsonValueKind.Object)
            {
                throw new SettingsError($"'settings' in '{path}' must be a JSON object.");
            }

            foreach (var prop in settingsEl.EnumerateObject())
            {
                payload[prop.Name] = prop.Value.Clone();
            }
        }

        Logger.Diagnostic($"{tierName} settings file found and parsed: '{path}'", DiagnosticsCategories.Settings);
        return true;
    }

    /// <summary>
    /// Reads the module-dir and working-directory settings.json's actual top-level properties (the
    /// "settings" object itself included, as just another entry; module first, working directory
    /// overriding it key-by-key), then the working directory's settings.local.json's own top-level
    /// properties overriding that. Distinct from <see cref="LoadPayload"/>, which reaches inside
    /// "settings" instead of returning it whole -- <see cref="Section"/> is the only caller of this
    /// method.
    /// </summary>
    private static Dictionary<string, JsonElement> LoadTopLevel(string modulePath, string path, string localPath)
    {
        var payload = new Dictionary<string, JsonElement>();

        var moduleFound = MergeTopLevelObject(payload, modulePath, "module");
        var pathFound = MergeTopLevelObject(payload, path, "working directory");

        if (!moduleFound && !pathFound)
        {
            // Section()'s own consumer effect is narrower than Load()'s -- neither tier having a
            // file just means whatever section the caller asked for comes back empty, not a
            // run-wide behavior change -- so Info rather than Load()'s Warning for the same
            // both-missing condition.
            Logger.Info($"no settings file found at module path '{modulePath}' or working directory '{path}'; {nameof(Section)} returns an empty section.", DiagnosticsCategories.Settings);
        }

        if (File.Exists(localPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    payload[prop.Name] = prop.Value.Clone();
                }

                Logger.Diagnostic($"local settings override found and parsed: '{localPath}'", DiagnosticsCategories.Settings);
            }
            else
            {
                Logger.Warning($"local settings file '{localPath}' exists but its root is not a JSON object; ignoring it.", DiagnosticsCategories.Settings);
            }
        }
        else
        {
            Logger.Diagnostic($"no local settings override at '{localPath}'.", DiagnosticsCategories.Settings);
        }

        return payload;
    }

    /// <summary>
    /// Merges one file's whole top-level object into <paramref name="payload"/> (key-by-key).
    /// <see cref="LoadTopLevel"/>'s counterpart to <see cref="MergeSettingsObject"/> -- same
    /// "strict" (malformed file throws) treatment, just without reaching inside a "settings"
    /// sub-object first. Returns whether the file existed.
    /// </summary>
    private static bool MergeTopLevelObject(Dictionary<string, JsonElement> payload, string path, string tierName)
    {
        if (!File.Exists(path))
        {
            Logger.Diagnostic($"no {tierName} settings file at '{path}'.", DiagnosticsCategories.Settings);
            return false;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new SettingsError($"'{path}' must contain a JSON object.");
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            payload[prop.Name] = prop.Value.Clone();
        }

        Logger.Diagnostic($"{tierName} settings file found and parsed: '{path}'", DiagnosticsCategories.Settings);
        return true;
    }

    private static bool TryParseLevel(string text, out TelemetryLevel level)
    {
        switch (text)
        {
            case "verbose":
                level = TelemetryLevel.Verbose;
                return true;
            case "info":
                level = TelemetryLevel.Info;
                return true;
            case "warning":
                level = TelemetryLevel.Warning;
                return true;
            case "error":
                level = TelemetryLevel.Error;
                return true;
            case "critical":
                level = TelemetryLevel.Critical;
                return true;
            default:
                level = TelemetryLevel.Error;
                return false;
        }
    }
}
