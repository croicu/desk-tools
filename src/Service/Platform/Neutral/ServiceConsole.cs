using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64 -- genuinely unset only
// happens now if src/Directory.Build.props' own win-x64 default gets explicitly cleared (a plain
// dotnet build/test/run picks up that default like any other build; see its own remarks).
// A no-op here, same reasoning as Platform/Linux/ServiceConsole.cs: with no RID to say otherwise,
// there's no subsystem distinction to bridge, so doing nothing is the safe default.
internal sealed class ServiceConsole : IConsole
{
    public void EnsureConsole()
    {
    }

    public void ReleaseConsole()
    {
    }
}
