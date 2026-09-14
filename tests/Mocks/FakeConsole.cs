using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Mocks;

/// <summary>
/// In-memory <see cref="IConsole"/> fake for tests -- records call order/counts instead of
/// touching the real console, unlike Service's real <c>ServiceConsole</c>.
/// </summary>
public sealed class FakeConsole : IConsole
{
    public int EnsureConsoleCalls { get; private set; }

    public int ReleaseConsoleCalls { get; private set; }

    public void EnsureConsole() => EnsureConsoleCalls++;

    public void ReleaseConsole() => ReleaseConsoleCalls++;
}
