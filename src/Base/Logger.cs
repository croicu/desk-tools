using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Base;

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
/// Static facade over a list of sinks (<see cref="SetLogger"/> adds one, passing null removes the
/// most-recently-added) so a call site can install/uninstall sinks temporarily (e.g. tests) without
/// threading them through every call. Every output method here (<see cref="Log"/>,
/// <see cref="Info"/>, <see cref="Print"/>, etc.) invokes ALL currently-registered sinks
/// sequentially, in registration order -- this is a genuine fan-out, not a last-one-wins override:
/// each sink gets its own independent copy of every event (each <see cref="DiagnosticsLog"/>
/// owns its own pending buffer -- see its class doc comment -- specifically so N active sinks don't
/// corrupt or duplicate-write into a shared one). Use this, not <see cref="Console.WriteLine(string)"/>
/// directly -- see CLAUDE.md's Logging section for level/category guidance. The list itself is
/// scoped to the logical call context (see <see cref="SinksLocal"/>), not truly process-wide, so a
/// host serving multiple heterogeneous clients from one process can give each client's context its
/// own independent set of sinks.
///
/// If nothing else has installed a sink yet, the default is a live <see cref="ConsoleLog"/>
/// with bootstrap settings, not the inert base <see cref="DiagnosticsLog"/> -- a deliberate
/// smell: it couples this generic facade to one concrete, console-printing implementation, purely
/// so a simple single-threaded console app never has to call
/// <see cref="ConsoleLog.Create"/>/<see cref="SetLogger"/>/dispose it itself just to get
/// working output (see <see cref="ConfigureConsole"/>). A host that wants something else for its
/// own context and calls <see cref="SetLogger"/> as the very first thing it does with
/// <see cref="Logger"/> avoids that default being constructed at all -- see
/// <see cref="SetLogger"/>'s own remarks.
/// </summary>
public static class Logger
{
    private static readonly AsyncLocal<List<DiagnosticsLog>?> SinksLocal = new();

    private static List<DiagnosticsLog> Sinks => SinksLocal.Value ??= [];

    public static void SetLogger(DiagnosticsLog? value)
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
    /// Reconfigures every registered sink's level/category filtering that happens to be a
    /// <see cref="ConsoleLog"/> -- a no-op for any other <see cref="DiagnosticsLog"/>
    /// subclass a host may have added via <see cref="SetLogger"/>. At most one
    /// <see cref="ConsoleLog"/> can ever be registered at a time (see its own instance guard),
    /// so this reconfigures at most one, but finds it regardless of registration order. This is
    /// what lets a simple single-threaded console app apply its real settings.json-derived
    /// level/categories without ever calling
    /// <see cref="ConsoleLog.Create"/>/<see cref="SetLogger"/> itself -- the default sink (see
    /// <see cref="ActiveSinks"/>) already exists with safe bootstrap settings; this just narrows it
    /// once real settings are available.
    /// </summary>
    public static void ConfigureConsole(TelemetryLevel minLevel, List<string>? categories = null, List<string>? excludedCategories = null)
    {
        foreach (var sink in ActiveSinks())
        {
            if (sink is ConsoleLog consoleSink)
            {
                consoleSink.Configure(minLevel, categories, excludedCategories);
            }
        }
    }

    /// <summary>
    /// Logs to every registered sink and returns the first sink's record (they're equivalent in
    /// level/message/category regardless of which sink produced them -- only the timestamp could
    /// differ by microseconds -- so "first" is just a stable, arbitrary pick for callers like
    /// <see cref="AppError"/> that need exactly one record back).
    /// </summary>
    public static TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        TelemetryRecord? first = null;
        foreach (var sink in ActiveSinks())
        {
            var record = sink.Log(level, message, category);
            first ??= record;
        }

        return first!;
    }

    public static void Flush()
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Flush();
        }
    }

    public static void Clear()
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Clear();
        }
    }

    /// <summary>
    /// Concatenates every registered sink's own drained messages, in registration order. With more
    /// than one sink active, the same logical event appears once per sink that received it (each
    /// sink independently recorded it) -- this is the fan-out model working as designed, not
    /// deduplicated into one canonical log.
    /// </summary>
    public static List<string> Drain()
    {
        var messages = new List<string>();
        foreach (var sink in ActiveSinks())
        {
            messages.AddRange(sink.Drain());
        }

        return messages;
    }

    public static void Print(string message)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Print(message);
        }
    }

    public static void Diagnostic(string message, string category = DiagnosticsCategories.General)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Diagnostic(message, category);
        }
    }

    public static void Info(string message, string category = DiagnosticsCategories.General)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Info(message, category);
        }
    }

    public static void Warning(string message, string category = DiagnosticsCategories.General)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Warning(message, category);
        }
    }

    public static void Error(string message, string category = DiagnosticsCategories.General)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Error(message, category);
        }
    }

    public static void Fatal(string message, string category = DiagnosticsCategories.General)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Fatal(message, category);
        }
    }

    public static void Perf(string description, double elapsedSeconds)
    {
        foreach (var sink in ActiveSinks())
        {
            sink.Perf(description, elapsedSeconds);
        }
    }

    /// <summary>
    /// Test-only: disposes every currently-registered sink (releasing a live
    /// <see cref="ConsoleLog"/>'s guard, if any -- otherwise a leftover instance from an earlier
    /// test would make the next test's own <see cref="ConsoleLog.Create"/> throw) and clears the
    /// list back to empty, so the next actual log call re-seeds a fresh default via
    /// <see cref="ActiveSinks"/>.
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
    /// The full set of currently-registered sinks -- lazily creates a live
    /// <see cref="ConsoleLog"/> with bootstrap settings if the list is empty (see the class doc
    /// comment above), rather than <see cref="Sinks"/> itself doing that. This matters for
    /// <see cref="SetLogger"/>: registering a caller's own sink as literally the first thing this
    /// context does with <see cref="Logger"/> must not force the default into existence first only
    /// to sit alongside it -- only an actual attempt to log (reaching this method) should trigger
    /// it.
    /// </summary>
    private static List<DiagnosticsLog> ActiveSinks()
    {
        if (Sinks.Count == 0)
        {
            Sinks.Add(ConsoleLog.Create());
        }

        return Sinks;
    }
}
