using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64 -- notably, when it's unset
// entirely (a plain dotnet build/test/run, or the IDE's own design-time build for IntelliSense).
// See Platform/Linux/HandleDuplicator.cs's own remarks for why this throws rather than no-ops --
// same reasoning as that variant, just for "no RID specified" rather than "Linux specifically".
internal static class HandleDuplicator
{
    internal static (long Stdin, long Stdout) Duplicate(ToolProcess tool, int targetProcessId)
    {
        throw new AppError("handle duplication is not supported on this platform yet -- see issue #32.");
    }
}
