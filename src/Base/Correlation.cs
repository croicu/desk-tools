namespace Croicu.Desk.Tools.Base;

/// <summary>
/// AsyncLocal-scoped correlation id for the current logical operation -- Architecture convention
/// 10. Lazily generated the first time it's read within a given async call context (mirrors
/// Logger's own default-sink laziness in Logger.cs), so nested awaits/Tasks spawned from one
/// operation share the same id, while an unrelated concurrent operation (a different client, in a
/// future multi-client host) gets its own. A short opaque hex id, not a GUID's full form -- this is
/// for a human skimming a log file, not a globally-unique identifier persisted anywhere.
///
/// Primarily consumed by <see cref="Sinks.FileLog"/> to tag each line, since a log file -- unlike
/// the console -- is read later, disconnected from which concurrent operation actually produced
/// which interleaved line. Deliberately its own leaf type rather than living on <see cref="Context"/>
/// (which already exposes similar AsyncLocal-scoped ambient state): <see cref="Sinks.FileLog"/>
/// needs to read it, and <see cref="Context.Create"/> already constructs sinks from
/// <c>Croicu.Desk.Tools.Base.Sinks</c> -- putting it on <see cref="Context"/> instead would make
/// <c>Sinks</c> depend back on <see cref="Context"/>, a cycle Architecture convention 9 asks us to
/// avoid even within one project.
/// </summary>
public static class Correlation
{
    private static readonly AsyncLocal<string?> CurrentLocal = new();

    public static string Current => CurrentLocal.Value ??= Guid.NewGuid().ToString("N")[..8];
}
