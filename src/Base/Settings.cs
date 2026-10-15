using System.Text.Json;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Layered JSON settings: the module's own directory's <c>settings.json</c> (the base -- e.g. the
/// file shipped alongside an installed build), then -- for <see cref="Section"/> only -- the per-user
/// file (<see cref="UserSettings"/>), then the module directory's <c>settings.local.json</c>
/// overriding all of that (a personal, gitignored override file -- see .gitignore). Every default
/// location is next to the running module, never relative to the current working directory: Service
/// is elevated, so a cwd-relative file would let whoever can write there change its core settings
/// (issue #70). The explicit <c>path</c> parameters exist for tests and embedding hosts, which choose
/// the file themselves; nothing reads one by default. Load once via <see cref="Load"/>, then read back via
/// <see cref="Current"/>. <see cref="Load"/> (the core <c>"settings"</c> object) never reads the
/// per-user file: it is user-writable but read by an elevated Service, so it may only supply
/// non-core sections (see <see cref="UserSettings"/>). Implements <see cref="ISettingsProvider"/> so
/// a caller that accepts that interface works unmodified with a real, file-backed instance.
/// </summary>
public sealed record Settings : ISettingsProvider
{
    private const int DefaultPort = 51823;

    // AppContext.BaseDirectory is fixed for the lifetime of the process, so this is computed once
    // rather than re-resolved on every Load()/Section() call.
    private static readonly string DefaultModulePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    // Next to the module, like DefaultModulePath -- deliberately not the working directory (issue #70).
    private static readonly string DefaultLocalPath = Path.Combine(AppContext.BaseDirectory, "settings.local.json");

    // AsyncLocal rather than a plain static field: scopes "the current settings" to the logical
    // call context (flows across await continuations, doesn't leak into an unrelated concurrent
    // Load()/Current pair) -- same reasoning as ConsoleLog's InstanceActive in Diagnostics.cs.
    private static readonly AsyncLocal<Settings?> CurrentInstance = new();

    public bool Debug { get; }
    public TelemetryLevel LogLevel { get; }
    public List<string> LogCategories { get; }
    public List<string> ExcludedCategories { get; }
    public int IdleTimeout { get; }
    public string? LogDir { get; }
    public int Port { get; }
    public bool AllowCallerRegistry { get; }

    private Settings(
        bool debug,
        TelemetryLevel logging,
        List<string> logCategories,
        List<string> excludedCategories,
        int idleTimeout,
        string? logDir,
        int port,
        bool allowCallerRegistry)
    {
        Debug = debug;
        LogLevel = logging;
        LogCategories = logCategories;
        ExcludedCategories = excludedCategories;
        IdleTimeout = idleTimeout;
        LogDir = logDir;
        Port = port;
        AllowCallerRegistry = allowCallerRegistry;
    }

    public static Settings Current => CurrentInstance.Value ?? throw new InvalidOperationException("Settings.Load() must be called first.");

    /// <summary>
    /// <paramref name="path"/> is an optional extra <c>settings.json</c> layered over the module one
    /// (tests, embedding hosts); null (the default) means none -- there is no cwd lookup (issue #70).
    /// <paramref name="localPath"/> defaults to <c>settings.local.json</c> next to the module.
    /// </summary>
    public static Settings Load(string? path = null, string? localPath = null, string? modulePath = null)
    {
        var debug = false;
        var logLevel = TelemetryLevel.Error;
        var logCategories = new List<string>();
        var excludedCategories = new List<string>();
        var expandCategories = false;
        var idleTimeout = 600;
        string? logDir = null;
        var port = DefaultPort;
        var allowCallerRegistry = false;

        var payload = LoadPayload(modulePath ?? DefaultModulePath, path, localPath ?? DefaultLocalPath);

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

            if (payload.TryGetValue("logDir", out var logDirEl))
            {
                if (logDirEl.ValueKind != JsonValueKind.String)
                {
                    throw new SettingsError("'settings.logDir' in settings.json must be a string.");
                }

                logDir = logDirEl.GetString();
            }

            if (payload.TryGetValue("port", out var portEl))
            {
                if (portEl.ValueKind != JsonValueKind.Number)
                {
                    throw new SettingsError("'settings.port' in settings.json must be a number.");
                }

                port = portEl.GetInt32();
            }

            if (payload.TryGetValue("allowCallerRegistry", out var allowCallerRegistryEl))
            {
                if (allowCallerRegistryEl.ValueKind != JsonValueKind.True && allowCallerRegistryEl.ValueKind != JsonValueKind.False)
                {
                    throw new SettingsError("'settings.allowCallerRegistry' in settings.json must be true or false.");
                }

                allowCallerRegistry = allowCallerRegistryEl.GetBoolean();
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

        var settings = new Settings(debug, logLevel, logCategories, excludedCategories, idleTimeout, logDir, port, allowCallerRegistry);
        CurrentInstance.Value = settings;

        return settings;
    }

    /// <summary>
    /// Returns one named top-level section of the settings files, merged key-by-key across the tiers
    /// in order -- module <c>settings.json</c>, the optional explicit <paramref name="path"/>, the
    /// per-user file (<paramref name="userPath"/>, default <see cref="UserSettings.DefaultPath"/>),
    /// then <c>settings.local.json</c> (default: next to the module) -- later tiers overriding earlier
    /// ones. No tier is looked up relative to the working directory (issue #70). E.g. a plugin's or
    /// subsystem's own config section living as a sibling of the core "settings" object, not nested
    /// inside it. settings.json's shape is uniform across consumer repos, so this is the public entry
    /// point for a caller outside this class to reach its own section without knowing how the files
    /// are read or merged.
    ///
    /// The module and explicit-path tiers are strict (invalid JSON, or the section not being an
    /// object, throws <see cref="SettingsError"/>); <c>settings.local.json</c> is lenient about bad
    /// JSON (logged and ignored) but a non-object section still throws; the per-user tier is lenient
    /// about both, since any process running as the user can write it and a bad file must not take
    /// down an elevated Service. The per-user tier is never read for the core <c>"settings"</c>
    /// section -- see <see cref="UserSettings"/>.
    /// </summary>
    public static Dictionary<string, JsonElement> Section(string name, string? path = null, string? localPath = null, string? modulePath = null, string? userPath = null)
    {
        modulePath ??= DefaultModulePath;
        localPath ??= DefaultLocalPath;

        var result = new Dictionary<string, JsonElement>();

        var moduleFound = MergeSection(result, name, modulePath, "module", lenientFile: false, lenientShape: false);
        var pathFound = path is not null && MergeSection(result, name, path, "explicit", lenientFile: false, lenientShape: false);

        if (!moduleFound && !pathFound)
        {
            // Section()'s own consumer effect is narrower than Load()'s -- neither tier having a
            // file just means whatever section the caller asked for comes back empty (or from the
            // personal tiers below), not a run-wide behavior change -- so Info rather than Load()'s
            // Warning for the same both-missing condition.
            Logger.Info($"no settings file found at module path '{modulePath}'{DescribeExplicit(path)}; {nameof(Section)} relies on the remaining tiers.", DiagnosticsCategories.Settings);
        }

        var resolvedUserPath = userPath ?? UserSettings.DefaultPath;
        if (string.Equals(name, UserSettings.CoreSection, StringComparison.Ordinal))
        {
            Logger.Diagnostic($"per-user settings are not honored for the core '{name}' section; skipping that tier.", DiagnosticsCategories.Settings);
        }
        else if (resolvedUserPath is null)
        {
            Logger.Diagnostic("no per-user settings location on this platform; skipping that tier.", DiagnosticsCategories.Settings);
        }
        else
        {
            MergeSection(result, name, resolvedUserPath, "per-user", lenientFile: true, lenientShape: true);
        }

        MergeSection(result, name, localPath, "local", lenientFile: true, lenientShape: false);

        return result;
    }

    /// <summary>
    /// Merges one file's named top-level section into <paramref name="result"/> key-by-key. Returns
    /// whether the file existed. <paramref name="lenientFile"/> turns invalid JSON, or a root that
    /// isn't an object, from a <see cref="SettingsError"/> into a logged warning (the tier is ignored);
    /// <paramref name="lenientShape"/> does the same for the section itself not being an object.
    /// </summary>
    private static bool MergeSection(Dictionary<string, JsonElement> result, string name, string filePath, string tierName, bool lenientFile, bool lenientShape)
    {
        if (!File.Exists(filePath))
        {
            Logger.Diagnostic($"no {tierName} settings file at '{filePath}'.", DiagnosticsCategories.Settings);
            return false;
        }

        using var doc = lenientFile ? TryParseFile(filePath) : ParseFileStrict(filePath);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            if (!lenientFile)
            {
                throw new SettingsError($"'{filePath}' must contain a JSON object.");
            }

            // Invalid JSON syntax (doc is null, see TryParseFile) or valid JSON in the wrong shape --
            // ignored by design for the personal/user tiers, but surfaced rather than silent.
            Logger.Warning($"{tierName} settings file '{filePath}' exists but is not a JSON object (or not valid JSON); ignoring it.", DiagnosticsCategories.Settings);
            return true;
        }

        if (doc.RootElement.TryGetProperty(name, out var sectionEl))
        {
            if (sectionEl.ValueKind != JsonValueKind.Object)
            {
                if (!lenientShape)
                {
                    throw new SettingsError($"'{name}' in '{filePath}' must be a JSON object.");
                }

                Logger.Warning($"'{name}' in {tierName} settings file '{filePath}' is not a JSON object; ignoring it.", DiagnosticsCategories.Settings);
                return true;
            }

            foreach (var prop in sectionEl.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }
        }

        Logger.Diagnostic($"{tierName} settings file found and parsed: '{filePath}'", DiagnosticsCategories.Settings);
        return true;
    }

    /// <summary>
    /// Reads the module-dir settings.json's "settings" object, then the optional explicit
    /// <paramref name="path"/>'s overriding it key-by-key, then the local file's own "settings"
    /// object (default: next to the module) overriding that. Private -- Section() is the public way
    /// for a caller outside this class to reach a section of the merged file.
    /// </summary>
    private static Dictionary<string, JsonElement> LoadPayload(string modulePath, string? path, string localPath)
    {
        var payload = new Dictionary<string, JsonElement>();

        var moduleFound = MergeSettingsObject(payload, modulePath, "module");
        var pathFound = path is not null && MergeSettingsObject(payload, path, "explicit");

        if (!moduleFound && !pathFound)
        {
            // Load()'s own consumer effect: with no settings.json at either base tier, every knob
            // it reads (debug, logLevel, logCategories, excludedCategories, idleTimeout, logDir)
            // falls back to its own restrictive default -- a meaningful behavior change (e.g.
            // Verbose/Info logging is unreachable for the rest of the run without one), so this is
            // Warning rather than Info.
            Logger.Warning($"no settings file found at module path '{modulePath}'{DescribeExplicit(path)}; falling back to restrictive defaults (debug=false, logLevel=error).", DiagnosticsCategories.Settings);
        }

        if (File.Exists(localPath))
        {
            using var doc = TryParseFile(localPath);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object &&
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
                // Unlike a malformed module/explicit-path settings.json (which throws), a
                // malformed settings.local.json -- invalid JSON syntax (doc is null, see
                // TryParseFile) or valid JSON in the wrong shape -- is silently ignored by design.
                // Silent was the wrong call by itself (a typo'd override would otherwise vanish with
                // zero signal), so surface it as a Warning instead of leaving it truly silent.
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

    private static string DescribeExplicit(string? path)
    {
        return path is null ? string.Empty : $" or explicit path '{path}'";
    }

    /// <summary>
    /// Merges one settings.json-shaped file's "settings" object into <paramref name="payload"/>
    /// (key-by-key, overwriting anything already present under the same key). Shared by both the
    /// module-dir and explicit-path tiers in <see cref="LoadPayload"/> -- unlike
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

        using var doc = ParseFileStrict(path);
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
    /// Parses a file as JSON, returning null (rather than throwing) if it isn't valid JSON syntax --
    /// used only for the lenient settings.local.json tier, where a malformed file is meant to be
    /// logged and ignored, not thrown. <see cref="ParseFileStrict"/> is the counterpart for the
    /// module-dir/explicit-path tiers, which are meant to throw instead.
    /// </summary>
    private static JsonDocument? TryParseFile(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a file as JSON, converting invalid JSON syntax into a <see cref="SettingsError"/>
    /// (consistent with the shape-mismatch <see cref="SettingsError"/>s already thrown right after
    /// each call site) instead of letting the raw <see cref="JsonException"/> propagate unhandled.
    /// Used only for the strict module-dir/explicit-path tiers -- <see cref="TryParseFile"/> is
    /// the counterpart for the lenient settings.local.json tier.
    /// </summary>
    private static JsonDocument ParseFileStrict(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException error)
        {
            throw new SettingsError($"'{path}' is not valid JSON: {error.Message}");
        }
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
