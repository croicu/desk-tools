namespace Croicu.Desk.Tools.Base.Sinks;

/// <summary>
/// Console-printing sink with level/category filtering. See Settings.cs / CLAUDE.md's Logging
/// section for how <c>settings.json</c> maps onto <see cref="minLevel"/>/<see cref="categories"/>/
/// <see cref="excludedCategories"/>.
/// </summary>
public sealed class ConsoleLog : DiagnosticsLog
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

    private ConsoleLog(TelemetryLevel minLevel, List<string>? categories, List<string>? excludedCategories)
    {
        _minLevel = minLevel;
        _categories = categories;
        _excludedCategories = excludedCategories;
    }

    /// <summary>
    /// Factory instead of a public constructor -- see <see cref="InstanceActive"/>: at most one
    /// ConsoleLog may be live per logical call context at a time, so callers can't accidentally
    /// construct a second one (e.g. via a bare <c>new</c>) while the first is still installed there.
    /// Dispose the returned instance to allow creating another in that same context.
    /// </summary>
    public static ConsoleLog Create(TelemetryLevel minLevel = TelemetryLevel.Error, List<string>? categories = null, List<string>? excludedCategories = null)
    {
        if (InstanceActive.Value)
        {
            throw new InvalidOperationException("A ConsoleLog is already active -- dispose it before creating another.");
        }

        InstanceActive.Value = true;
        return new ConsoleLog(minLevel, categories, excludedCategories);
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
