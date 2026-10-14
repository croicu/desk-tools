using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Every test passes explicit paths for all four settings tiers (never the real %LOCALAPPDATA% or the
/// working directory), so these run in parallel and never touch the developer's own files.
/// </summary>
[TestClass]
public sealed class DatabasePathStoreTests
{
    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"db-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DatabasePathStore StoreFor(string dir, string? workingDirectoryJson = null)
    {
        string? working = null;
        if (workingDirectoryJson is not null)
        {
            working = Path.Combine(dir, "settings.json");
            File.WriteAllText(working, workingDirectoryJson);
        }

        return new DatabasePathStore(
            userPath: Path.Combine(dir, "user.json"),
            path: working ?? Path.Combine(dir, "settings.json"),
            localPath: Path.Combine(dir, "local.json"),
            modulePath: Path.Combine(dir, "module.json"));
    }

    [TestMethod]
    public void Get_NothingConfigured_ReturnsNull()
    {
        Assert.IsNull(StoreFor(NewDir()).Get());
    }

    [TestMethod]
    public void Set_ThenGet_RoundTripsAWindowsPath()
    {
        var store = StoreFor(NewDir());
        const string path = @"C:\Users\someone\OneDrive - Personal\Secrets.kdbx";

        store.Set(path);

        Assert.AreEqual(path, store.Get());
    }

    [TestMethod]
    public void Set_WritesThePerUserFileAndKeepsOtherSettings()
    {
        var dir = NewDir();
        var userPath = Path.Combine(dir, "user.json");
        File.WriteAllText(userPath, """{ "other": { "x": "1" } }""");
        var store = StoreFor(dir);

        store.Set(@"D:\db\Secrets.kdbx");

        Assert.AreEqual("1", Settings.Section("other", userPath: userPath, path: Path.Combine(dir, "none.json"), localPath: Path.Combine(dir, "none2.json"), modulePath: Path.Combine(dir, "none3.json"))["x"].GetString());
        Assert.AreEqual(@"D:\db\Secrets.kdbx", store.Get());
    }

    [TestMethod]
    public void Get_ValueFromAnotherTier_IsFoundButThePerUserValueWinsOverIt()
    {
        var dir = NewDir();
        var store = StoreFor(dir, """{ "secrets": { "databasePath": "C:\\from\\working\\dir.kdbx" } }""");
        Assert.AreEqual(@"C:\from\working\dir.kdbx", store.Get());

        store.Set(@"C:\from\user.kdbx");

        Assert.AreEqual(@"C:\from\user.kdbx", store.Get());
    }

    [TestMethod]
    public void Get_ValueIsNotAString_IsTreatedAsUnset()
    {
        var store = StoreFor(NewDir(), """{ "secrets": { "databasePath": 5 } }""");

        Assert.IsNull(store.Get());
    }

    [TestMethod]
    public void Description_NamesTheSettingAndThePerUserFile()
    {
        var dir = NewDir();

        var description = StoreFor(dir).Description;

        Assert.Contains("secrets.databasePath", description);
        Assert.Contains(Path.Combine(dir, "user.json"), description);
    }
}
