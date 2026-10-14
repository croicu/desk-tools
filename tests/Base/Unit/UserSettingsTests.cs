using System.Text.Json;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// The per-user settings tier (<see cref="UserSettings"/> plus <see cref="Settings.Section"/>'s
/// merge of it). Every test works in its own throwaway directory and passes explicit paths, never
/// the real %LOCALAPPDATA%, so these run safely in parallel and never touch the developer's own file.
/// </summary>
[TestClass]
public sealed class UserSettingsTests
{
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"user-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string name, string json)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Missing(string dir, string name) => Path.Combine(dir, name);

    private static Dictionary<string, JsonElement> ReadSection(string name, string dir, string? module = null, string? cwd = null, string? user = null, string? local = null)
    {
        return Settings.Section(
            name,
            path: cwd ?? Missing(dir, "cwd.json"),
            localPath: local ?? Missing(dir, "local.json"),
            modulePath: module ?? Missing(dir, "module.json"),
            userPath: user ?? Missing(dir, "user.json"));
    }

    // --- Section(): the merge ---

    [TestMethod]
    public void Section_UserTier_OverridesWorkingDirectoryAndModuleKeyByKey()
    {
        var dir = NewDir();
        var module = Write(dir, "module.json", """{ "s": { "a": "module", "b": "module" } }""");
        var cwd = Write(dir, "cwd.json", """{ "s": { "b": "cwd", "c": "cwd" } }""");
        var user = Write(dir, "user.json", """{ "s": { "c": "user", "d": "user" } }""");

        var section = ReadSection("s", dir, module, cwd, user);

        Assert.AreEqual("module", section["a"].GetString());
        Assert.AreEqual("cwd", section["b"].GetString());
        Assert.AreEqual("user", section["c"].GetString());
        Assert.AreEqual("user", section["d"].GetString());
    }

    [TestMethod]
    public void Section_LocalOverride_BeatsTheUserTier()
    {
        var dir = NewDir();
        var user = Write(dir, "user.json", """{ "s": { "k": "user", "only-user": "x" } }""");
        var local = Write(dir, "local.json", """{ "s": { "k": "local" } }""");

        var section = ReadSection("s", dir, user: user, local: local);

        Assert.AreEqual("local", section["k"].GetString());
        Assert.AreEqual("x", section["only-user"].GetString());
    }

    [TestMethod]
    public void Section_CoreSettingsSection_IsNeverReadFromTheUserTier()
    {
        var dir = NewDir();
        var cwd = Write(dir, "cwd.json", """{ "settings": { "logLevel": "info" } }""");
        var user = Write(dir, "user.json", """{ "settings": { "logDir": "C:/somewhere/evil", "logLevel": "verbose" } }""");

        var section = ReadSection("settings", dir, cwd: cwd, user: user);

        Assert.AreEqual("info", section["logLevel"].GetString());
        Assert.IsFalse(section.ContainsKey("logDir"), "the user tier must not be able to set core settings.");
    }

    [TestMethod]
    public void Section_UserFileNotValidJson_IsIgnoredNotThrown()
    {
        var dir = NewDir();
        var cwd = Write(dir, "cwd.json", """{ "s": { "k": "cwd" } }""");
        var user = Write(dir, "user.json", "{ this is not json");

        var section = ReadSection("s", dir, cwd: cwd, user: user);

        Assert.AreEqual("cwd", section["k"].GetString());
    }

    [TestMethod]
    public void Section_UserSectionNotAnObject_IsIgnoredNotThrown()
    {
        var dir = NewDir();
        var cwd = Write(dir, "cwd.json", """{ "s": { "k": "cwd" } }""");
        var user = Write(dir, "user.json", """{ "s": "not-an-object" }""");

        var section = ReadSection("s", dir, cwd: cwd, user: user);

        Assert.AreEqual("cwd", section["k"].GetString());
    }

    [TestMethod]
    public void Section_NoFilesAtAll_IsEmpty()
    {
        Assert.IsEmpty(ReadSection("s", NewDir()));
    }

    // --- UserSettings.Set ---

    [TestMethod]
    public void Set_NewFile_CreatesFolderAndFileAndReadsBackThroughSection()
    {
        var dir = NewDir();
        var user = Path.Combine(dir, "nested", "Desk Tools", "settings.json");

        UserSettings.Set("secrets", "databasePath", "D:/db/secrets.kdbx", user);

        Assert.IsTrue(File.Exists(user));
        Assert.AreEqual("D:/db/secrets.kdbx", ReadSection("secrets", dir, user: user)["databasePath"].GetString());
    }

    [TestMethod]
    public void Set_PathWithBackslashes_RoundTripsExactly()
    {
        var dir = NewDir();
        var user = Path.Combine(dir, "user.json");
        const string value = @"C:\Users\someone\OneDrive - Personal\Secrets.kdbx";

        UserSettings.Set("secrets", "databasePath", value, user);

        Assert.AreEqual(value, ReadSection("secrets", dir, user: user)["databasePath"].GetString());
    }

    [TestMethod]
    public void Set_KeepsEveryOtherSectionAndKey()
    {
        var dir = NewDir();
        var user = Write(dir, "user.json", """{ "secrets": { "other": "keep" }, "elsewhere": { "x": "1" }, "settings": { "debug": true } }""");

        UserSettings.Set("secrets", "databasePath", "D:/db.kdbx", user);

        var secrets = ReadSection("secrets", dir, user: user);
        Assert.AreEqual("keep", secrets["other"].GetString());
        Assert.AreEqual("D:/db.kdbx", secrets["databasePath"].GetString());
        Assert.AreEqual("1", ReadSection("elsewhere", dir, user: user)["x"].GetString());
        using var doc = JsonDocument.Parse(File.ReadAllText(user));
        Assert.IsTrue(doc.RootElement.GetProperty("settings").GetProperty("debug").GetBoolean(), "an unrelated (even if ignored) section must be preserved verbatim.");
    }

    [TestMethod]
    public void Set_ExistingKey_IsOverwritten()
    {
        var dir = NewDir();
        var user = Path.Combine(dir, "user.json");
        UserSettings.Set("secrets", "databasePath", "first", user);

        UserSettings.Set("secrets", "databasePath", "second", user);

        Assert.AreEqual("second", ReadSection("secrets", dir, user: user)["databasePath"].GetString());
    }

    [TestMethod]
    public void Set_CoreSection_ThrowsAndWritesNothing()
    {
        var dir = NewDir();
        var user = Path.Combine(dir, "user.json");

        var error = Assert.ThrowsExactly<SettingsError>(() => UserSettings.Set("settings", "logDir", "/x", user));

        Assert.Contains("core settings", error.Message);
        Assert.IsFalse(File.Exists(user));
    }

    [TestMethod]
    [DataRow("", "k")]
    [DataRow("s", "")]
    [DataRow("  ", "k")]
    public void Set_EmptySectionOrKey_Throws(string section, string key)
    {
        var user = Path.Combine(NewDir(), "user.json");

        Assert.ThrowsExactly<SettingsError>(() => UserSettings.Set(section, key, "v", user));
    }

    [TestMethod]
    public void Set_ExistingFileNotValidJson_ThrowsAndLeavesItUntouched()
    {
        var dir = NewDir();
        var user = Write(dir, "user.json", "{ the user's half-edited file");

        var error = Assert.ThrowsExactly<SettingsError>(() => UserSettings.Set("secrets", "databasePath", "D:/db.kdbx", user));

        Assert.Contains("not valid JSON", error.Message);
        Assert.AreEqual("{ the user's half-edited file", File.ReadAllText(user));
    }

    [TestMethod]
    public void Set_SectionNotAnObject_ThrowsAndLeavesItUntouched()
    {
        var dir = NewDir();
        const string original = """{ "secrets": "oops" }""";
        var user = Write(dir, "user.json", original);

        Assert.ThrowsExactly<SettingsError>(() => UserSettings.Set("secrets", "databasePath", "D:/db.kdbx", user));

        Assert.AreEqual(original, File.ReadAllText(user));
    }

    [TestMethod]
    public void Set_LeavesNoTemporaryFilesBehind()
    {
        var dir = NewDir();
        var user = Path.Combine(dir, "user.json");

        UserSettings.Set("secrets", "databasePath", "a", user);
        UserSettings.Set("secrets", "databasePath", "b", user);

        CollectionAssert.AreEqual(new[] { user }, Directory.GetFiles(dir));
    }

    [TestMethod]
    public void Set_DestinationBrieflyHeldOpenByAnotherProcess_RetriesAndSucceeds()
    {
        // Antivirus/indexers briefly open the destination without FILE_SHARE_DELETE (readers are fine, a replace is not), so the
        // final move fails with ACCESS_DENIED on Windows. Simulate that for ~200ms.
        var dir = NewDir();
        var user = Write(dir, "user.json", "{}");
        var held = new FileStream(user, FileMode.Open, FileAccess.Read, FileShare.Read);

        void WriteWhileHeld() => UserSettings.Set("secrets", "databasePath", "D:/data.txt", user);

        var writing = Task.Run(WriteWhileHeld);
        Thread.Sleep(200);
        held.Dispose();

        Assert.IsTrue(writing.Wait(TimeSpan.FromSeconds(10)), "the write never completed.");
        Assert.AreEqual("D:/data.txt", ReadSection("secrets", dir, user: user)["databasePath"].GetString());
    }

    [TestMethod]
    public void DefaultPath_IsDeskToolsSettingsUnderLocalApplicationData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            Assert.IsNull(UserSettings.DefaultPath);
            return;
        }

        Assert.AreEqual(Path.Combine(local, "Desk Tools", "settings.json"), UserSettings.DefaultPath);
    }
}
