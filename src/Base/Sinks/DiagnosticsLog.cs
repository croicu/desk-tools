namespace Croicu.Desk.Tools.Base.Sinks;

/// <summary>
/// Base sink: records everything into its own pending buffer (one per instance -- see
/// <see cref="Logger"/>'s class doc comment for why this matters once more than one sink is
/// registered there) and does no filtering of its own. <see cref="ConsoleLog"/> adds
/// level/category filtering on top; a host application can supply an entirely different subclass
/// instead -- e.g. one backed by an open file handle, which is why this implements
/// <see cref="IDisposable"/>: a caller installing a sink via <see cref="Logger.SetLogger"/> should
/// always own it with a <c>using</c> so a file-backed subclass gets to close its handle.
///
/// <see cref="Print"/>/<see cref="Flush"/> write through an injected <see cref="TextWriter"/> pair
/// (defaulting to <see cref="Console.Out"/>/<see cref="Console.Error"/>) rather than calling
/// <see cref="Console"/> directly -- the same "Explicit DI First" seam <see cref="DebugLog"/>'s own
/// injectable <c>write</c> delegate already uses, here so a test can construct a sink pointed at
/// its own <see cref="StringWriter"/> instead of redirecting the real, process-wide
/// <see cref="Console.Out"/>/<see cref="Console.SetOut"/> (which no two tests touching it can do at
/// the same time without <c>[DoNotParallelize]</c>).
/// </summary>
public class DiagnosticsLog : ILoggingSink, IDisposable
{
    private readonly List<TelemetryRecord> _pending = [];
    private readonly TextWriter _printWriter;
    private readonly TextWriter _errorWriter;

    public DiagnosticsLog(TextWriter? printWriter = null, TextWriter? errorWriter = null)
    {
        _printWriter = printWriter ?? Console.Out;
        _errorWriter = errorWriter ?? Console.Error;
    }

    public virtual TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        var record = new TelemetryRecord(DateTimeOffset.UtcNow, level, message, category);
        _pending.Add(record);
        return record;
    }

    /// <summary>
    /// Writes every pending record to <see cref="_errorWriter"/> as "{timestamp}: {message}". The
    /// Python template routes this through stdlib logging at a fixed WARNING level regardless of
    /// each record's own level; this is the zero-dependency equivalent (stderr by default, not
    /// stdout, to mirror routing somewhere other than the sink's own normal Print channel).
    /// </summary>
    public void Flush()
    {
        foreach (var record in _pending)
        {
            _errorWriter.WriteLine($"{record.Timestamp:O}: {record.Message}");
        }
    }

    public void Clear() => _pending.Clear();

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
        foreach (var record in _pending)
        {
            messages.Add(record.Message);
        }

        _pending.Clear();
        return messages;
    }

    /// <summary>
    /// Raw, unconditional output -- no level/category filtering, no <c>[LEVEL][category]</c>
    /// prefix. For text that isn't really a log message but must always reach the user regardless
    /// of the configured <c>logLevel</c> (e.g. <c>--help</c> usage text). Virtual so a sink whose
    /// channel isn't the real console (<see cref="DebugLog"/>) can route this somewhere else
    /// instead of inheriting a write through <see cref="_printWriter"/> -- without that, a sink
    /// fanned out alongside a "real" printing sink (<see cref="ConsoleLog"/>, or Hello's own
    /// <c>DiagnosticsLog</c>) would duplicate every <see cref="Logger.Print"/> call onto stdout.
    /// </summary>
    public virtual void Print(string message) => _printWriter.WriteLine(message);

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
