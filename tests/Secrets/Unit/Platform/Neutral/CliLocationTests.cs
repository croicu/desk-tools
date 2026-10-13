using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Compiled in whenever the test project is built with a RuntimeIdentifier other than
/// win-x64/linux-x64 (see tests/Secrets/Secrets.Tests.csproj's Platform/-selection ItemGroups) --
/// e.g. `DESK_TOOLS_RID` cleared. Pairs with <c>CliLocation</c>'s Neutral variant, which always
/// throws <see cref="AppError"/>; this verifies that "not supported yet" contract.
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
