using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Compiled in for the non-Windows variants of the test project (see tests/Secrets/Secrets.Tests.csproj's Platform/
/// selection). The placeholder <c>ProtectedPasswordStore</c> always throws <see cref="AppError"/>: remembering the master
/// password needs a platform facility that keeps it from every unelevated process, and none is decided for this platform
/// yet (issue #51). A real implementation grows this file.
/// </summary>
[TestClass]
public sealed class ProtectedPasswordStoreTests
{
    [TestMethod]
    public void EveryOperation_ThrowsAppError()
    {
        var store = new ProtectedPasswordStore();

        Assert.ThrowsExactly<AppError>(() => store.Load("/data/my.kdbx"));
        Assert.ThrowsExactly<AppError>(() => store.Save("/data/my.kdbx", [1, 2, 3]));
        Assert.ThrowsExactly<AppError>(() => store.Forget());
    }
}
