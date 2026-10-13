using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Only compiled in when the test project is built for linux-x64 (see
/// tests/Secrets/Secrets.Tests.csproj's Platform/-selection ItemGroups): run with
/// `DESK_TOOLS_RID=linux-x64 dotnet test`. <c>CliLocation</c>'s Linux variant is a placeholder that
/// always throws, so this verifies that documented contract; a real Linux implementation (a
/// separate task) grows this file.
/// </summary>
[TestClass]
public sealed class CliLocationTests
{
    [TestMethod]
    public void Resolve_ThrowsAppError()
    {
        Assert.ThrowsExactly<AppError>(() => CliLocation.Resolve());
    }
}
