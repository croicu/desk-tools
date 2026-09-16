using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

// Mirrors Platform/Windows/HandleDuplicator.cs's own API shape so Host's mcp method needs no
// #if/OperatingSystem.IsWindows() branch, same reasoning as SingletonGuard's/ServiceConsole's own
// split. Unlike SingletonGuard's Linux variant (a safe no-op, since there was nothing to guard
// against yet on that platform), a no-op here would be actively wrong: the caller genuinely needs
// real, usable handle values back, and silently returning fake success would just break whatever
// asked for them one step later. A real Linux implementation would need a different mechanism
// entirely (e.g. SCM_RIGHTS ancillary data over a Unix domain socket, the closest Linux analog to
// what DuplicateHandle does on Windows) -- out of scope for issue #32.
internal static class HandleDuplicator
{
    internal static (long Stdin, long Stdout) Duplicate(ToolProcess tool, int targetProcessId)
    {
        throw new AppError("handle duplication is not supported on this platform (Linux) yet -- see issue #32.");
    }
}
