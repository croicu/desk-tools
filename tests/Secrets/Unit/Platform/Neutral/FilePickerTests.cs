using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Compiled in for the non-Windows variants of the test project (see
/// tests/Secrets/Secrets.Tests.csproj's Platform/ selection). <c>FilePicker</c>'s placeholder always
/// throws <see cref="AppError"/>; this verifies that documented contract, and a real implementation
/// (issue #51) grows this file.
/// </summary>
[TestClass]
public sealed class FilePickerTests
{
    [TestMethod]
    public void Pick_ThrowsAppError()
    {
        Assert.ThrowsExactly<AppError>(() => new FilePicker().Pick("title", "KeePass databases", "kdbx"));
    }
}
