using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Only compiled in when the test project itself is built for linux-x64 (see
/// tests/Service/Service.Tests.csproj's Platform/-selection ItemGroups) -- run explicitly with
/// `dotnet test -r linux-x64` to exercise these. Identical to
/// Platform/Neutral/HandleDuplicatorTests.cs: <c>HandleDuplicator</c>'s Linux variant
/// (<c>Platform/Linux/HandleDuplicator.cs</c>) is currently the same "not supported" throw as its
/// Neutral one, so there's nothing behaviorally different to test yet -- kept as its own file
/// rather than shared, matching the product code's own separate-but-identical Linux/Neutral split,
/// so a future real Linux implementation (see <c>Platform/Linux/HandleDuplicator.cs</c>'s own
/// remarks -- likely <c>SCM_RIGHTS</c> over a Unix domain socket) has its own test file ready to
/// grow into.
/// </summary>
[TestClass]
public sealed class HandleDuplicatorTests
{
    [TestMethod]
    public void Duplicate_ThrowsAppError()
    {
        using var tool = ToolLauncher.Launch("dotnet", ["--version"], new Dictionary<string, string>(), AppContext.BaseDirectory);

        Assert.ThrowsExactly<AppError>(() => HandleDuplicator.Duplicate(tool, targetProcessId: Environment.ProcessId));
    }
}
