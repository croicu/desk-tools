namespace Croicu.Desk.Tools.Base.Sinks;

/// <summary>
/// Writes every message to a file under a caller-supplied directory, named by the timestamp this
/// sink was created (see <see cref="Create"/>) -- unconditional, no level/category filtering, same
/// reasoning as <see cref="DebugLog"/>: a persisted log file is for later post-mortem debugging, not
/// real-time viewing, so it should capture everything regardless of how the console sink is
/// separately configured/filtered.
///
/// Every line -- from <see cref="Log"/> or <see cref="Print"/> alike -- is prefixed with its own
/// timestamp and the current logical operation's <see cref="Correlation.Current"/> id (a
/// <see cref="Log"/> line additionally carries level and category; a <see cref="Print"/> line
/// doesn't, since <see cref="Logger.Print"/> genuinely has neither). Unlike the console (read in
/// real time, one operation at a time), a log file is read later and can interleave lines from
/// several concurrent operations (e.g. several clients in a future multi-client host), so every
/// line -- including a raw <see cref="Print"/>'d one -- needs enough context to be attributed on
/// its own.
/// </summary>
public sealed class FileLog : DiagnosticsLog
{
    // Same per-context single-instance guard as ConsoleLog/DebugLog, and for the same reason --
    // see ConsoleLog's own remarks.
    private static readonly AsyncLocal<bool> InstanceActive = new();

    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    private FileLog(StreamWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Factory instead of a public constructor -- see <see cref="InstanceActive"/>. Creates
    /// <paramref name="logDir"/> if it doesn't already exist and opens a new file inside it named
    /// after the current UTC timestamp (millisecond precision, so two sinks created in rapid
    /// succession -- e.g. by two different clients in a future multi-client host -- don't collide).
    /// Dispose the returned instance to close the file and allow creating another in that same
    /// context.
    /// </summary>
    public static FileLog Create(string logDir)
    {
        if (InstanceActive.Value)
        {
            throw new InvalidOperationException("A FileLog is already active -- dispose it before creating another.");
        }

        InstanceActive.Value = true;

        Directory.CreateDirectory(logDir);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.log";
        var logPath = Path.Combine(logDir, fileName);
        var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };

        return new FileLog(writer);
    }

    public override void Dispose()
    {
        InstanceActive.Value = false;
        _writer.Dispose();
        base.Dispose();
    }

    public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        var record = base.Log(level, message, category);
        WriteLine($"[{record.Timestamp:O}][{Correlation.Current}][{level.ToString().ToUpperInvariant()}][{record.Category}] {record.Message}");
        return record;
    }

    /// <summary>
    /// Still prefixed with a timestamp and the current <see cref="Correlation.Current"/> id -- just
    /// without <c>[LEVEL][category]</c>, since <see cref="Logger.Print"/> genuinely has neither (see
    /// its own doc comment). Print()'d text (e.g. Hello's own protocol response lines) is exactly
    /// the kind of content the class doc comment's "attribute an interleaved line on its own"
    /// reasoning is for -- omitting the prefix here would defeat that purpose for every Print() call,
    /// even though <see cref="DebugLog.Print"/>'s reasoning for going unformatted (its destination,
    /// the debugger's Output window, isn't shared across concurrent operations the way a log file
    /// can be) doesn't actually apply to this sink.
    /// </summary>
    public override void Print(string message) => WriteLine($"[{DateTimeOffset.UtcNow:O}][{Correlation.Current}] {message}");

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            _writer.WriteLine(line);
        }
    }
}
