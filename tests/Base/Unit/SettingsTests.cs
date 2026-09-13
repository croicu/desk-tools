using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// Every test passes explicit path/localPath arguments to Settings.Load()/Section() rather than
/// relying on the current directory's settings.json -- each test also uses its own uniquely-named
/// temp file, so these run safely in parallel despite Settings.Load()'s Settings.Current side
/// effect (never read back here).
/// </summary>
[TestClass]
public sealed class SettingsTests
{
    /// <summary>
    /// Load()/Section() now log through Logger.Log() on every call (found-and-parsed, missing-file,
    /// malformed-local-override), which lazily creates Logger's default ConsoleLogSink (see
    /// Diagnostics.cs) if nothing else has already -- same reason ErrorsTests.cs resets, so a
    /// leftover live default here can't make some other test's own ConsoleLogSink.Create() throw.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Load_NoSettingsFile_DefaultsToRestrictiveErrorLevelAndGeneralCategory()
    {
        var settings = Settings.Load(path: NonExistentPath(), localPath: NonExistentPath());

        Assert.IsFalse(settings.Debug);
        Assert.AreEqual(TelemetryLevel.Error, settings.LogLevel);
        CollectionAssert.AreEqual(new List<string> { DiagnosticsCategories.General }, settings.LogCategories);
        Assert.IsEmpty(settings.ExcludedCategories);
    }

    [TestMethod]
    public void Load_NoSettingsFile_LogsWarningUnderSettingsCategory()
    {
        Logger.Drain();

        Settings.Load(path: NonExistentPath(), localPath: NonExistentPath());

        var messages = Logger.Drain();
        Assert.HasCount(2, messages);
        Assert.Contains("not found", messages[0]);
        Assert.Contains("no local settings override", messages[1]);
    }

    [TestMethod]
    public void Load_ValidSettingsFile_LogsThatItWasFoundAndParsed()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "info" } }""");
        try
        {
            Logger.Drain();

            Settings.Load(path: path, localPath: NonExistentPath());

            var messages = Logger.Drain();
            Assert.Contains("found and parsed", messages[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_MalformedLocalSettingsFile_LogsWarningAndIgnoresIt()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "info" } }""");
        var localPath = WriteTempSettingsFile("""{ "settings": "not-an-object" }""");
        try
        {
            Logger.Drain();

            var settings = Settings.Load(path: path, localPath: localPath);

            Assert.AreEqual(TelemetryLevel.Info, settings.LogLevel);
            var messages = Logger.Drain();
            Assert.Contains("exists but has no valid 'settings' object", messages[1]);
        }
        finally
        {
            File.Delete(path);
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public void Load_ExplicitVerboseLogLevel_ExpandsCategoriesEvenWithDebugFalse()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "debug": false, "logLevel": "verbose" } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath());

            Assert.AreEqual(TelemetryLevel.Verbose, settings.LogLevel);
            Assert.IsEmpty(settings.LogCategories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_ExplicitCriticalLogLevel_StaysRestrictiveEvenWithDebugTrue()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "debug": true, "logLevel": "critical" } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath());

            Assert.AreEqual(TelemetryLevel.Critical, settings.LogLevel);
            CollectionAssert.AreEqual(new List<string> { DiagnosticsCategories.General }, settings.LogCategories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_DebugTrueWithoutExplicitLogLevel_ExpandsCategories()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "debug": true } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath());

            Assert.IsTrue(settings.Debug);
            Assert.AreEqual(TelemetryLevel.Error, settings.LogLevel);
            Assert.IsEmpty(settings.LogCategories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_InvalidLogLevel_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "bogus" } }""");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_LogCategoriesNotArray_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logCategories": "not-an-array" } }""");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_ExcludedCategoriesNotArray_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "excludedCategories": "not-an-array" } }""");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_ExpandedCategoriesWithExplicitList_InsertsGeneralIfMissing()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "verbose", "logCategories": ["custom"] } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath());

            CollectionAssert.AreEqual(new List<string> { DiagnosticsCategories.General, "custom" }, settings.LogCategories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_LocalSettingsOverridesBaseKeyByKey()
    {
        var basePath = WriteTempSettingsFile("""{ "settings": { "debug": false, "logLevel": "error" } }""");
        var localPath = WriteTempSettingsFile("""{ "settings": { "logLevel": "verbose" } }""");
        try
        {
            var settings = Settings.Load(path: basePath, localPath: localPath);

            Assert.AreEqual(TelemetryLevel.Verbose, settings.LogLevel);
            Assert.IsFalse(settings.Debug);
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public void Section_ReturnsEmptyDictionaryWhenSectionMissing()
    {
        var path = WriteTempSettingsFile("""{ "settings": {} }""");
        try
        {
            var section = Settings.Section("nonexistent", path: path, localPath: NonExistentPath());

            Assert.IsEmpty(section);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Section_ReturnsMergedSectionValues()
    {
        var path = WriteTempSettingsFile("""{ "mySection": { "key": "value" } }""");
        try
        {
            var section = Settings.Section("mySection", path: path, localPath: NonExistentPath());

            Assert.IsTrue(section.ContainsKey("key"));
            Assert.AreEqual("value", section["key"].GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Section_ThrowsWhenSectionIsNotObject()
    {
        var path = WriteTempSettingsFile("""{ "mySection": "not-an-object" }""");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Section("mySection", path: path, localPath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempSettingsFile(string json)
    {
        var path = NonExistentPath();
        File.WriteAllText(path, json);
        return path;
    }

    private static string NonExistentPath() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
}
