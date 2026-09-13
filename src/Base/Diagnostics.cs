namespace Service.Base;

/// <summary>
/// Not a closed set: category is an open string so callers can introduce new categories without
/// editing this file. <see cref="General"/> is the only one every log call defaults to.
/// </summary>
public static class DiagnosticsCategories
{
    public const string General = "general";
    public const string Perf = "perf";
    public const string Settings = "settings";
}

/// <summary>
/// Declared in ascending severity order so relational comparisons (level &gt;= minLevel) do the
/// job Python's separate LEVEL_RANK dict existed for -- no lookup table needed here.
/// </summary>
public enum TelemetryLevel
{
    Verbose,
    Info,
    Warning,
    Error,
    Critical,
}

public sealed class TelemetryRecord
{
    public DateTimeOffset Timestamp { get; }
    public TelemetryLevel Level { get; }
    public string Message { get; }
    public string Category { get; }

    public TelemetryRecord(DateTimeOffset timestamp, TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
        Category = category;
    }
}

/// <summary>
/// Base sink: records everything into a pending buffer scoped to the logical call context (shared
/// across every instance within that context, including subclasses -- mirrors the Python
/// template's class-level _pending list, but per-context rather than truly process-wide, so one
/// client's buffer in a multi-client host doesn't leak into another's) and does no filtering of
/// its own. <see cref="ConsoleLogSink"/> below adds level/category filtering on top; a host
/// application can supply an entirely different subclass instead -- e.g. one backed by an open
/// file handle, which is why this implements <see cref="IDisposable"/>: a caller installing a sink
/// via <see cref="Logger.SetLogger"/> should always own it with a <c>using</c> so a file-backed
/// subclass gets to close its handle.
/// </summary>
public class DiagnosticsLogSink : ILoggingSink, IDisposable
{
    private static readonly AsyncLocal<List<TelemetryRecord>> PendingLocal = new();

    private static List<TelemetryRecord> Pending => PendingLocal.Value ??= new();

    public virtual TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        var record = new TelemetryRecord(DateTimeOffset.UtcNow, level, message, category);
        Pending.Add(record);
        return record;
    }

    /// <summary>
    /// Writes every pending record to stderr as "{timestamp}: {message}". The Python template
    /// routes this through stdlib logging at a fixed WARNING level regardless of each record's own
    /// level; this is the zero-dependency equivalent (stderr, not stdout, to mirror routing
    /// somewhere other than the sink's own normal Print channel).
    /// </summary>
    public void Flush()
    {
        foreach (var record in Pending)
        {
            Console.Error.WriteLine($"{record.Timestamp:O}: {record.Message}");
        }
    }

    public void Clear() => Pending.Clear();

    /// <summary>
    /// No-op on this base sink, which owns no unmanaged/file resources of its own. A subclass that
    /// does (e.g. a file-backed sink holding a <see cref="System.IO.StreamWriter"/>) overrides this
    /// to close it.
    /// </summary>
    public virtual void Dispose()
    {
    }

    public List<string> Drain()
    {
        var messages = new List<string>();
        foreach (var record in Pending)
        {
            messages.Add(record.Message);
        }

        Pending.Clear();
        return messages;
    }

    /// <summary>
    /// Raw, unconditional output -- no level/category filtering, no <c>[LEVEL][category]</c>
    /// prefix. For text that isn't really a log message but must always reach the user regardless
    /// of the configured <c>logLevel</c> (e.g. <c>--help</c> usage text).
    /// </summary>
    public void Print(string message) => Console.WriteLine(message);

    public void Diagnostic(string message, string category = DiagnosticsCategories.General) => Log(TelemetryLevel.Verbose, message, category);

    public void Info(string message, string category = DiagnosticsCategories.General) => Log(TelemetryLevel.Info, message, category);

    public void Warning(string message, string category = DiagnosticsCategories.General) => Log(TelemetryLevel.Warning, message, category);

    public void Error(string message, string category = DiagnosticsCategories.General) => Log(TelemetryLevel.Error, message, category);

    public void Fatal(string message, string category = DiagnosticsCategories.General) => Log(TelemetryLevel.Critical, message, category);

    /// <summary>
    /// Duration marker, always logged at Info under the fixed <see cref="DiagnosticsCategories.Perf"/>
    /// category (not the caller's choice, unlike every other method here).
    /// </summary>
    public void Perf(string description, double elapsedSeconds) =>
        Log(TelemetryLevel.Info, $"duration: {elapsedSeconds:F3}s - {description}", DiagnosticsCategories.Perf);
}

/// <summary>
/// Console-printing sink with level/category filtering. See Settings.cs / CLAUDE.md's Logging
/// section for how <c>settings.json</c> maps onto <see cref="minLevel"/>/<see cref="categories"/>/
/// <see cref="excludedCategories"/>.
/// </summary>
public sealed class ConsoleLogSink : DiagnosticsLogSink
{
    private static readonly object ConsoleLock = new();

    // Create()/Dispose() track whether an instance is currently live in the calling context so the
    // constructor can't be called again there until the first is disposed -- see Create()'s own
    // doc comment. AsyncLocal rather than a plain static bool: a host serving multiple
    // heterogeneous clients from one process needs each client's context to be able to have its own
    // live sink at the same time -- a process-wide flag would incorrectly block client B from
    // creating one just because client A already has.
    private static readonly AsyncLocal<bool> InstanceActive = new();

    private TelemetryLevel _minLevel;
    private List<string>? _categories;
    private List<string>? _excludedCategories;

    private ConsoleLogSink(TelemetryLevel minLevel, List<string>? categories, List<string>? excludedCategories)
    {
        _minLevel = minLevel;
        _categories = categories;
        _excludedCategories = excludedCategories;
    }

    /// <summary>
    /// Factory instead of a public constructor -- see <see cref="InstanceActive"/>: at most one
    /// ConsoleLogSink may be live per logical call context at a time, so callers can't accidentally
    /// construct a second one (e.g. via a bare <c>new</c>) while the first is still installed there.
    /// Dispose the returned instance to allow creating another in that same context.
    /// </summary>
    public static ConsoleLogSink Create(TelemetryLevel minLevel = TelemetryLevel.Error, List<string>? categories = null, List<string>? excludedCategories = null)
    {
        if (InstanceActive.Value)
        {
            throw new InvalidOperationException("A ConsoleLogSink is already active -- dispose it before creating another.");
        }

        InstanceActive.Value = true;
        return new ConsoleLogSink(minLevel, categories, excludedCategories);
    }

    /// <summary>
    /// Reconfigures level/category filtering in place. Lets a caller install this sink with safe
    /// bootstrap defaults before settings.json is even read (so nothing logged during that read --
    /// e.g. a malformed-settings error -- is silently dropped by the no-op base sink), then narrow
    /// it once real settings are available, without swapping sink instances mid-run.
    /// </summary>
    public void Configure(TelemetryLevel minLevel, List<string>? categories = null, List<string>? excludedCategories = null)
    {
        _minLevel = minLevel;
        _categories = categories;
        _excludedCategories = excludedCategories;
    }

    public override void Dispose()
    {
        InstanceActive.Value = false;
        base.Dispose();
    }

    public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        var record = base.Log(level, message, category);
        var levelPasses = level >= _minLevel;

        bool categoryPasses;
        if (_categories is { Count: > 0 })
        {
            // Explicit (or debug-widened) allow-list: excludedCategories is inert here -- a
            // category named in both would just be a no-op omission the caller could've made
            // directly in the allow-list instead.
            categoryPasses = _categories.Contains(record.Category);
        }
        else
        {
            // Unfiltered ("show everything") state: excludedCategories becomes a deny-list over
            // the otherwise-open set.
            categoryPasses = _excludedCategories is null || _excludedCategories.Count == 0 || !_excludedCategories.Contains(record.Category);
        }

        if (levelPasses && categoryPasses)
        {
            lock (ConsoleLock)
            {
                Console.WriteLine($"[{level.ToString().ToUpperInvariant()}][{record.Category}] {record.Message}");
            }
        }

        return record;
    }
}

/// <summary>
/// Static facade over a stack of sinks (<see cref="SetLogger"/> pushes, passing null pops) so a
/// call site can swap the active sink temporarily (e.g. tests) without threading it through every
/// call. Use this, not <see cref="Console.WriteLine(string)"/> directly -- see CLAUDE.md's Logging
/// section for level/category guidance. The stack itself is scoped to the logical call context
/// (see <see cref="SinksLocal"/>), not truly process-wide, so a host serving multiple heterogeneous
/// clients from one process can give each client's context its own independent sink/settings.
///
/// If nothing else has installed a sink yet, the default is a live <see cref="ConsoleLogSink"/>
/// with bootstrap settings, not the inert base <see cref="DiagnosticsLogSink"/> -- a deliberate
/// smell: it couples this generic facade to one concrete, console-printing implementation, purely
/// so a simple single-threaded console app never has to call
/// <see cref="ConsoleLogSink.Create"/>/<see cref="SetLogger"/>/dispose it itself just to get
/// working output (see <see cref="ConfigureConsole"/>). A host that wants something else for its
/// own context and calls <see cref="SetLogger"/> as the very first thing it does with
/// <see cref="Logger"/> avoids that default being constructed at all -- see
/// <see cref="SetLogger"/>'s own remarks.
/// </summary>
public static class Logger
{
    private static readonly AsyncLocal<List<DiagnosticsLogSink>?> SinksLocal = new();

    private static List<DiagnosticsLogSink> Sinks => SinksLocal.Value ??= [];

    public static void SetLogger(DiagnosticsLogSink? value)
    {
        if (value is null)
        {
            if (Sinks.Count > 0)
            {
                Sinks.RemoveAt(Sinks.Count - 1);
            }
        }
        else
        {
            Sinks.Add(value);
        }
    }

    /// <summary>
    /// Reconfigures the currently active sink's level/category filtering, if it's a
    /// <see cref="ConsoleLogSink"/> -- a no-op otherwise (e.g. a host pushed some other
    /// <see cref="DiagnosticsLogSink"/> subclass via <see cref="SetLogger"/>). This is what lets a
    /// simple single-threaded console app apply its real settings.json-derived level/categories
    /// without ever calling <see cref="ConsoleLogSink.Create"/>/<see cref="SetLogger"/> itself --
    /// the default sink (see <see cref="Sink"/>) already exists with safe bootstrap settings; this
    /// just narrows it once real settings are available.
    /// </summary>
    public static void ConfigureConsole(TelemetryLevel minLevel, List<string>? categories = null, List<string>? excludedCategories = null)
    {
        if (Sink() is ConsoleLogSink consoleSink)
        {
            consoleSink.Configure(minLevel, categories, excludedCategories);
        }
    }

    public static TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General) => Sink().Log(level, message, category);

    public static void Flush() => Sink().Flush();

    public static void Clear() => Sink().Clear();

    public static List<string> Drain() => Sink().Drain();

    public static void Print(string message) => Sink().Print(message);

    public static void Diagnostic(string message, string category = DiagnosticsCategories.General) => Sink().Diagnostic(message, category);

    public static void Info(string message, string category = DiagnosticsCategories.General) => Sink().Info(message, category);

    public static void Warning(string message, string category = DiagnosticsCategories.General) => Sink().Warning(message, category);

    public static void Error(string message, string category = DiagnosticsCategories.General) => Sink().Error(message, category);

    public static void Fatal(string message, string category = DiagnosticsCategories.General) => Sink().Fatal(message, category);

    public static void Perf(string description, double elapsedSeconds) => Sink().Perf(description, elapsedSeconds);

    /// <summary>
    /// Test-only: disposes every sink currently on the stack (releasing a live
    /// <see cref="ConsoleLogSink"/>'s guard, if any -- otherwise a leftover instance from an earlier
    /// test would make the next test's own <see cref="ConsoleLogSink.Create"/> throw) and clears the
    /// stack back to empty, so the next actual log call re-seeds a fresh default via
    /// <see cref="Sink"/>.
    /// </summary>
    internal static void Reset()
    {
        if (SinksLocal.Value is not null)
        {
            foreach (var sink in SinksLocal.Value)
            {
                sink.Dispose();
            }
        }

        SinksLocal.Value = null;
    }

    /// <summary>
    /// The currently active sink -- lazily creates a live <see cref="ConsoleLogSink"/> with
    /// bootstrap settings if the stack is empty (see the class doc comment above), rather than
    /// <see cref="Sinks"/> itself doing that. This matters for <see cref="SetLogger"/>: pushing a
    /// caller's own sink as literally the first thing this context does with <see cref="Logger"/>
    /// must not force the default into existence first only to shadow it -- only an actual attempt
    /// to log (reaching this method) should trigger it.
    /// </summary>
    private static DiagnosticsLogSink Sink()
    {
        if (Sinks.Count == 0)
        {
            Sinks.Add(ConsoleLogSink.Create());
        }

        return Sinks[^1];
    }
}
