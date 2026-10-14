namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class DatabasePathRuleTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"db-rule-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Touch(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [TestMethod]
    public void Check_ExistingLocalKdbx_IsAccepted()
    {
        Assert.IsNull(DatabasePathRule.Check(Touch(NewDir(), "Secrets.kdbx")));
    }

    [TestMethod]
    public void Check_ExtensionIsCaseInsensitive()
    {
        Assert.IsNull(DatabasePathRule.Check(Touch(NewDir(), "Secrets.KDBX")));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Check_EmptyPath_IsRejected(string? path)
    {
        Assert.Contains("empty", DatabasePathRule.Check(path)!);
    }

    [TestMethod]
    [DataRow(@"\\attacker\share\Secrets.kdbx")]
    [DataRow("//attacker/share/Secrets.kdbx")]
    [DataRow(@"\\?\UNC\attacker\share\Secrets.kdbx")]
    [DataRow(@"\\localhost\c$\Secrets.kdbx")]
    public void Check_UncPath_IsRejectedWithoutTouchingTheNetwork(string path)
    {
        // Must be refused on the text alone: even File.Exists on a UNC path would connect (and
        // authenticate) to the named server.
        Assert.Contains("UNC", DatabasePathRule.Check(path)!);
    }

    [TestMethod]
    public void Check_RelativePath_IsRejected()
    {
        Assert.Contains("not absolute", DatabasePathRule.Check("Secrets.kdbx")!);
    }

    [TestMethod]
    public void Check_WrongExtension_IsRejected()
    {
        var path = Touch(NewDir(), "Secrets.txt");

        Assert.Contains(".kdbx", DatabasePathRule.Check(path)!);
    }

    [TestMethod]
    public void Check_MissingFile_IsRejected()
    {
        var path = Path.Combine(NewDir(), "nope.kdbx");

        Assert.Contains("no such file", DatabasePathRule.Check(path)!);
    }

    [TestMethod]
    public void Check_DotDotSegments_AreResolvedBeforeTheExtensionCheck()
    {
        var dir = NewDir();
        Touch(dir, "real.kdbx");
        var tricky = Path.Combine(dir, "sub", "..", "real.kdbx");

        Assert.IsNull(DatabasePathRule.Check(tricky));
    }
}
