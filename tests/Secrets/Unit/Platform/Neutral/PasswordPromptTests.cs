using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Compiled in for the non-Windows variants of the test project (see
/// tests/Secrets/Secrets.Tests.csproj's Platform/ selection). <c>PasswordPrompt</c>'s placeholder
/// always throws <see cref="AppError"/>; this verifies that documented contract, and a real
/// implementation (issue #51) grows this file.
/// </summary>
[TestClass]
public sealed class PasswordPromptTests
{
    [TestMethod]
    public void Ask_ThrowsAppError()
    {
        Assert.ThrowsExactly<AppError>(() => new PasswordPrompt().Ask(@"C:\data\my.kdbx", 0, 3));
    }
}
