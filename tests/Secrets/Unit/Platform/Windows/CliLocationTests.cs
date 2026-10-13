using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class CliLocationTests
{
    private static string Combine(params string[] parts) => Path.Combine(parts);

    [TestMethod]
    public void IsUnder_PathInsideRoot_IsTrue()
    {
        var root = Combine(Path.GetTempPath(), "pf");

        Assert.IsTrue(CliLocation.IsUnder(Combine(root, "KeePassXC", "x.exe"), root));
    }

    [TestMethod]
    public void IsUnder_SiblingWithSamePrefix_IsFalse()
    {
        var root = Combine(Path.GetTempPath(), "pf");

        Assert.IsFalse(CliLocation.IsUnder(Combine(Path.GetTempPath(), "pf-evil", "x.exe"), root));
    }

    [TestMethod]
    public void IsUnder_DotDotEscape_IsFalse()
    {
        var root = Combine(Path.GetTempPath(), "pf");

        Assert.IsFalse(CliLocation.IsUnder(Combine(root, "..", "elsewhere", "x.exe"), root));
    }

    [TestMethod]
    public void Resolve_FileMissing_ThrowsWithInstallHint()
    {
        var root = Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var error = Assert.ThrowsExactly<AppError>(() => CliLocation.Resolve(root));

        Assert.Contains("not found", error.Message);
        Assert.Contains("machine-wide", error.Message);
    }

    [TestMethod]
    public void Resolve_FilePresent_ReturnsItsAbsolutePath()
    {
        var root = Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}");
        var expected = Path.GetFullPath(Combine(root, CliLocation.RelativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, string.Empty);

        Assert.AreEqual(expected, CliLocation.Resolve(root));
    }
}
