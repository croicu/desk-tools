namespace Croicu.Desk.Tools.Service;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64 -- notably, when it's unset
// entirely (a plain dotnet build/test/run, or the IDE's own design-time build for IntelliSense).
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
