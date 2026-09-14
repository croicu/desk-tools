using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Hello;

/// <summary>
/// No-op <see cref="IConsole"/> -- Hello is a plain stdio server with no real console-lifecycle
/// concerns of its own (unlike Service's <c>ServiceConsole</c>, which does real Windows
/// console-attach/hide work), so <see cref="Context.Start"/> still gets something to bracket
/// without this needing to actually do anything.
/// </summary>
internal sealed class NoOpConsole : IConsole
{
    public void EnsureConsole()
    {
    }

    public void ReleaseConsole()
    {
    }
}
