using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// No-op <see cref="IConsole"/> -- Desk is an ordinary console app, already attached to a real
/// console by the OS with no special attach/hide concerns of its own (unlike Service's
/// <c>ServiceConsole</c>), so <see cref="Context.Start"/> still gets something to bracket without
/// this needing to actually do anything. Same reasoning as Hello's own <c>VoidConsole</c>, for a
/// different reason (Hello is headless; Desk is already console-attached).
/// </summary>
internal sealed class VoidConsole : IConsole
{
    public void EnsureConsole()
    {
    }

    public void ReleaseConsole()
    {
    }
}
