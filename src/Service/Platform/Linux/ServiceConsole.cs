namespace Croicu.Desk.Tools.Service;

// Mirrors Platform/Windows/ServiceConsole.cs's class name so Program.cs's call site needs no
// #if/OperatingSystem.IsWindows() branch.
internal sealed class ServiceConsole : IConsole
{
    public void EnsureConsole()
    {
        // No-op: a Linux console app is already attached to its invoking terminal (or has none),
        // and there's no WinExe-style hidden-subsystem distinction to bridge here.
    }

    public void ReleaseConsole()
    {
        // No-op, same reasoning as EnsureConsole() above.
    }
}
