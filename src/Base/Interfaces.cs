namespace Croicu.Desk.Tools.Base;

// Public contracts: persisted/shared data (plain classes/records, no behavior) plus behavioral
// interfaces meant for a consumer to actually implement/inject (as opposed to Contracts.cs's
// interfaces, which wire this project's own internals together and aren't meant for an external
// consumer to implement).

/// <summary>
/// Injectable logging contract -- if this project is ever consumed as a library by another
/// project (rather than run standalone), the host application can pass its own logger into your
/// public constructors/factories and you write through it instead of your own private Logger.
/// Mirrors <see cref="DiagnosticsLog"/>'s method surface.
///
/// Note the language gap from the Python template this was ported from: Python's typing.Protocol
/// is structural, so DiagnosticsLog satisfied LoggingSink automatically with zero declaration
/// needed. A C# interface is nominal instead -- DiagnosticsLog has to explicitly declare
/// <c>: ILoggingSink</c> (see Sinks/DiagnosticsLog.cs) to satisfy this contract. Once declared,
/// callers see the same "no glue code needed" behavior the Python version had.
///
/// category defaults to the literal "general" here (not DiagnosticsCategories.General) so this
/// file has no outgoing dependency on Diagnostics.cs -- see Architecture convention 9 (acyclic
/// dependency graph) in CLAUDE.md.
/// </summary>
public interface ILoggingSink
{
    void Diagnostic(string message, string category = "general");

    void Info(string message, string category = "general");

    void Warning(string message, string category = "general");

    void Error(string message, string category = "general");

    void Fatal(string message, string category = "general");

    void Perf(string description, double elapsedSeconds);
}

/// <summary>
/// Injectable settings contract -- lets a consumer (an in-memory test fake, or a future host
/// application) supply its own settings without going through <see cref="Settings"/>'s real
/// file-based <see cref="Settings.Load"/>. <see cref="Settings"/> itself implements this, so
/// existing callers that already hold a <see cref="Settings"/> instance need no change.
/// </summary>
public interface ISettingsProvider
{
    bool Debug { get; }

    TelemetryLevel LogLevel { get; }

    List<string> LogCategories { get; }

    List<string> ExcludedCategories { get; }

    int IdleTimeout { get; }

    /// <summary>
    /// Directory a <see cref="Sinks.FileLog"/> should write its timestamped log file into, or null
    /// for no file logging -- see <see cref="Context.Create"/>. Named with a "Dir" suffix (not
    /// "Path") since it's a directory, not a full file name -- the sink itself picks the actual
    /// file name/path within it.
    /// </summary>
    string? LogDir { get; }
}

/// <summary>
/// Injectable console-lifecycle contract -- lets a host supply its own platform-specific
/// console-attach behavior (e.g. Service's <c>ServiceConsole</c> under <c>src/Service/Platform/</c>,
/// best-effort <c>AttachConsole</c> on Windows, no-op elsewhere) to <see cref="Context.Start"/>
/// without <c>src/Base</c> ever referencing a consuming app's own namespace -- Architecture
/// convention 9. <see cref="EnsureConsole"/> is called before anything that might log;
/// <see cref="ReleaseConsole"/> always runs afterward, even on failure.
/// </summary>
public interface IConsole
{
    void EnsureConsole();

    void ReleaseConsole();
}
