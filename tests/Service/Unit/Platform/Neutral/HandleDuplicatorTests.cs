using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Compiled in whenever the test project itself is built with no RuntimeIdentifier (the default
/// `dotnet test` invocation -- see tests/Service/Service.Tests.csproj's Platform/-selection
/// ItemGroups), which pairs with <c>HandleDuplicator</c>'s own Neutral variant
/// (<c>Platform/Neutral/HandleDuplicator.cs</c>) being what actually gets compiled into
/// <c>Service.csproj</c> under that same condition -- always throws <see cref="AppError"/>, same as
/// <c>Platform/Linux/HandleDuplicator.cs</c>. This verifies that documented "not supported yet"
/// contract, not the real handle handoff (only the Windows variant has that -- see
/// <c>Platform/Windows/HandleDuplicatorTests.cs</c>, which requires an explicit
/// `dotnet test -r win-x64` to actually compile and run).
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
