namespace Croicu.Desk.Tools.Base.Sinks;

/// <summary>
/// Writes every message to the attached debugger's Debug Output window (via
/// <see cref="System.Diagnostics.Debug.WriteLine(string)"/>) -- unconditional, no level/category
/// filtering, since the point of running this alongside <see cref="ConsoleLog"/> (see
/// <see cref="Context.Create"/>, which installs one automatically when its resolved
/// <see cref="Context.Debug"/> is true) is to see everything regardless of how the console sink is
/// separately configured/filtered.
///
/// <see cref="System.Diagnostics.Debug.WriteLine(string)"/> calls are compiled out entirely in a
/// Release build (the BCL method itself is <c>[Conditional("DEBUG")]</c>), so this sink is
/// inert there regardless of <c>settings.debug</c> at runtime -- consistent with its name and
/// purpose (a debug-build diagnostic aid), not a bug to work around.
/// </summary>
public sealed class DebugLog : DiagnosticsLog
{
    // Same per-context single-instance guard as ConsoleLog, and for the same reason -- see its
    // own remarks.
    private static readonly AsyncLocal<bool> InstanceActive = new();

    private readonly Action<string> _write;

    private DebugLog(Action<string> write, TextWriter? basePrintWriter)
        : base(printWriter: basePrintWriter)
    {
        _write = write;
    }

    /// <summary>
    /// Factory instead of a public constructor -- see <see cref="InstanceActive"/>: at most one
    /// DebugLog may be live per logical call context at a time. Dispose the returned instance
    /// to allow creating another in that same context.
    ///
    /// <paramref name="write"/> defaults to <see cref="System.Diagnostics.Debug.WriteLine(string)"/>
    /// and exists as an injectable seam per CLAUDE.md's "Explicit DI First" convention -- this is a
    /// component that talks to the outside world (the debug output channel), and
    /// System.Diagnostics.Debug.Listeners/TextWriterTraceListener aren't available without the
    /// separate System.Diagnostics.TraceSource package in this TFM, so tests inject a collecting
    /// delegate here instead of pulling in that dependency just to observe output.
    ///
    /// <paramref name="basePrintWriter"/> is a second, narrower test-only seam: <see cref="Print"/>
    /// is overridden below and never calls the inherited <see cref="DiagnosticsLog.Print"/>, so this
    /// parameter has no effect on this sink's own real behavior -- it exists only so a regression
    /// test can pass its own <see cref="StringWriter"/> as the base class's default-Console channel
    /// and assert it stayed empty, proving <see cref="Print"/> never fell through to it, without
    /// redirecting the real, process-wide <see cref="Console.Out"/> to do so.
    /// </summary>
    public static DebugLog Create(Action<string>? write = null, TextWriter? basePrintWriter = null)
    {
        if (InstanceActive.Value)
        {
            throw new InvalidOperationException("A DebugLog is already active -- dispose it before creating another.");
        }

        InstanceActive.Value = true;

        // A direct method-group reference to Debug.WriteLine doesn't compile -- [Conditional]
        // methods can only be invoked as statements, not captured as a delegate, since the
        // compiler needs to be able to elide the call entirely in a Release build. Wrapping it in
        // a trivial pass-through lambda (no branching, matches the "inert factory delegate"
        // exception to the no-lambdas-for-logic rule) still gets elided the same way in Release --
        // the call inside is stripped, leaving a no-op delegate.
        return new DebugLog(write ?? (message => System.Diagnostics.Debug.WriteLine(message)), basePrintWriter);
    }

    public override void Dispose()
    {
        InstanceActive.Value = false;
        base.Dispose();
    }

    public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
    {
        var record = base.Log(level, message, category);
        _write($"[{level.ToString().ToUpperInvariant()}][{record.Category}] {record.Message}");
        return record;
    }

    /// <summary>
    /// Routes through the same injectable channel as <see cref="Log"/> instead of inheriting
    /// <see cref="DiagnosticsLog.Print"/>'s write straight to <see cref="Console"/> -- this sink's
    /// whole point is to never touch the real console, and it's always fanned out alongside a sink
    /// that already does (<see cref="ConsoleLog"/>, or a host's own printing sink), so falling
    /// through to the base implementation would duplicate every <see cref="Logger.Print"/> call
    /// onto stdout.
    /// </summary>
    public override void Print(string message) => _write(message);
}
