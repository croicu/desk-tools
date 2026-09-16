namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Only compiled in when the test project itself is built for linux-x64 (see
/// tests/Service/Service.Tests.csproj's Platform/-selection ItemGroups) -- win-x64 is
/// tests/Directory.Build.props' own default now, so run explicitly with
/// `DESK_TOOLS_RID=linux-x64 dotnet test` (or `dotnet test tests/Service/Service.Tests.csproj -r
/// linux-x64` for just this one project) to exercise these. Identical to
/// Platform/Neutral/SingletonGuardTests.cs: <c>SingletonGuard</c>'s Linux variant
/// (<c>Platform/Linux/SingletonGuard.cs</c>) is currently the same no-op as its Neutral one, so
/// there's nothing behaviorally different to test yet -- kept as its own file rather than shared,
/// matching the product code's own separate-but-identical Linux/Neutral split, so a future real
/// Linux implementation (see <c>Platform/Linux/SingletonGuard.cs</c>'s own remarks -- likely a
/// `systemd --user` service) has its own test file ready to grow into.
/// </summary>
[TestClass]
public sealed class SingletonGuardTests
{
    [TestMethod]
    public void TryAcquire_AlwaysSucceeds()
    {
        using var guard = SingletonGuard.TryAcquire(out var abandoned);

        Assert.IsNotNull(guard);
        Assert.IsFalse(abandoned);
    }

    [TestMethod]
    public void TryAcquire_CalledTwiceWithoutReleasing_BothSucceed()
    {
        using var first = SingletonGuard.TryAcquire(out _);
        using var second = SingletonGuard.TryAcquire(out var abandoned);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsFalse(abandoned);
    }

    [TestMethod]
    public void Dispose_DoesNotThrow()
    {
        var guard = SingletonGuard.TryAcquire(out _);
        Assert.IsNotNull(guard);

        guard.Dispose();
    }
}
