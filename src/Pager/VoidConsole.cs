using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Pager;

/// <summary>
/// No-op <see cref="IConsole"/> -- Pager is a plain stdio server with no console-lifecycle concerns of its
/// own, so <see cref="Context.Start"/> still gets something to bracket without it doing anything.
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
