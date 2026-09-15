namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Compiled in whenever the test project itself is built with no RuntimeIdentifier (the default
/// `dotnet test` invocation -- see tests/Service/Service.Tests.csproj's Platform/-selection
/// ItemGroups, mirroring src/Service/Service.csproj's own), which pairs with
/// <c>SingletonGuard</c>'s own Neutral variant (<c>Platform/Neutral/SingletonGuard.cs</c>) being
/// what actually gets compiled into <c>Service.csproj</c> under that same condition -- a no-op that
/// always "acquires" successfully, same as <c>Platform/Linux/SingletonGuard.cs</c>. These tests
/// verify that documented no-op contract, not real cross-process exclusion (only the Windows
/// variant has that -- see <c>Platform/Windows/SingletonGuardTests.cs</c>, which requires an
/// explicit `dotnet test -r win-x64` to actually compile and run).
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
        // Unlike the real (Windows) implementation, the no-op provides no exclusion at all -- a
        // second acquisition succeeds even while the first is still held, which is exactly the
        // documented "nothing to guard against yet" contract this variant exists to provide.
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
