using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// Every test passes explicit path/localPath/modulePath arguments to Settings.Load()/Section()
/// rather than relying on the current directory or AppContext.BaseDirectory's real settings.json --
/// each test also uses its own uniquely-named temp file, so these run safely in parallel despite
/// Settings.Load()'s Settings.Current side effect (never read back here).
/// </summary>
[TestClass]
public sealed class SettingsTests
{
    /// <summary>
    /// Load()/Section() now log through Logger.Log() on every call (found-and-parsed, missing-file,
    /// malformed-local-override), which lazily creates Logger's default ConsoleLog (see
    /// Diagnostics.cs) if nothing else has already -- same reason ErrorsTests.cs resets, so a
    /// leftover live default here can't make some other test's own ConsoleLog.Create() throw.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Logger.Reset();

    [TestMethod]
    public void Load_NoSettingsFile_DefaultsToRestrictiveErrorLevelAndGeneralCategory()
    {
        var settings = Settings.Load(path: NonExistentPath(), localPath: NonExistentPath(), modulePath: NonExistentPath());

        Assert.IsFalse(settings.Debug);
        Assert.AreEqual(TelemetryLevel.Error, settings.LogLevel);
        CollectionAssert.AreEqual(new List<string> { DiagnosticsCategories.General }, settings.LogCategories);
        Assert.IsEmpty(settings.ExcludedCategories);
        Assert.AreEqual(600, settings.IdleTimeout);
    }

    [TestMethod]
    public void Load_ExplicitIdleTimeout_UsesConfiguredValue()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "idleTimeout": 5 } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

            Assert.AreEqual(5, settings.IdleTimeout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_IdleTimeoutNotNumber_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "idleTimeout": "not-a-number" } }""");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_NoSettingsFile_LogsWarningUnderSettingsCategory()
    {
        Logger.Drain();

        Settings.Load(path: NonExistentPath(), localPath: NonExistentPath(), modulePath: NonExistentPath());

        var messages = Logger.Drain();
        Assert.HasCount(4, messages);
        Assert.Contains("no module settings file", messages[0]);
        Assert.Contains("no working directory settings file", messages[1]);
        Assert.Contains("falling back to restrictive defaults", messages[2]);
        Assert.Contains("no local settings override", messages[3]);
    }

    [TestMethod]
    public void Load_ValidSettingsFile_LogsThatItWasFoundAndParsed()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "info" } }""");
        try
        {
            Logger.Drain();

            Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

            var messages = Logger.Drain();
            Assert.Contains("found and parsed", messages[1]);
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

            var settings = Settings.Load(path: path, localPath: localPath, modulePath: NonExistentPath());

            Assert.AreEqual(TelemetryLevel.Info, settings.LogLevel);
            var messages = Logger.Drain();
            Assert.Contains("exists but has no valid 'settings' object", messages[2]);
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
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
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
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
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
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Distinct from the *-NotArray/InvalidLogLevel tests above (valid JSON, wrong shape) -- this
    /// is invalid JSON *syntax*, which must also become a SettingsError rather than an unhandled
    /// JsonException escaping Load().
    /// </summary>
    [TestMethod]
    public void Load_WorkingDirectoryFileNotValidJson_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("not valid json");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_ModulePathFileNotValidJson_ThrowsSettingsError()
    {
        var modulePath = WriteTempSettingsFile("not valid json");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Load(path: NonExistentPath(), localPath: NonExistentPath(), modulePath: modulePath));
        }
        finally
        {
            File.Delete(modulePath);
        }
    }

    /// <summary>
    /// Unlike the strict module/working-directory tiers above, an invalid-JSON-syntax
    /// settings.local.json is logged and ignored, not thrown -- same lenient treatment as a
    /// valid-JSON-wrong-shape local file (see Load_MalformedLocalSettingsFile_LogsWarningAndIgnoresIt).
    /// </summary>
    [TestMethod]
    public void Load_LocalSettingsFileNotValidJson_LogsWarningAndIgnoresIt()
    {
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "info" } }""");
        var localPath = WriteTempSettingsFile("not valid json");
        try
        {
            var settings = Settings.Load(path: path, localPath: localPath, modulePath: NonExistentPath());

            Assert.AreEqual(TelemetryLevel.Info, settings.LogLevel);
        }
        finally
        {
            File.Delete(path);
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public void Section_WorkingDirectoryFileNotValidJson_ThrowsSettingsError()
    {
        var path = WriteTempSettingsFile("not valid json");
        try
        {
            Assert.ThrowsExactly<SettingsError>(() => Settings.Section("mySection", path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
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
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            var settings = Settings.Load(path: basePath, localPath: localPath, modulePath: NonExistentPath());

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
    public void Load_ModulePathUsedWhenWorkingDirectoryFileMissing()
    {
        var modulePath = WriteTempSettingsFile("""{ "settings": { "logLevel": "verbose", "idleTimeout": 7 } }""");
        try
        {
            var settings = Settings.Load(path: NonExistentPath(), localPath: NonExistentPath(), modulePath: modulePath);

            Assert.AreEqual(TelemetryLevel.Verbose, settings.LogLevel);
            Assert.AreEqual(7, settings.IdleTimeout);
        }
        finally
        {
            File.Delete(modulePath);
        }
    }

    [TestMethod]
    public void Load_WorkingDirectoryOverridesModulePathKeyByKey()
    {
        var modulePath = WriteTempSettingsFile("""{ "settings": { "debug": false, "logLevel": "error", "idleTimeout": 7 } }""");
        var path = WriteTempSettingsFile("""{ "settings": { "logLevel": "verbose" } }""");
        try
        {
            var settings = Settings.Load(path: path, localPath: NonExistentPath(), modulePath: modulePath);

            Assert.AreEqual(TelemetryLevel.Verbose, settings.LogLevel);
            Assert.IsFalse(settings.Debug);
            Assert.AreEqual(7, settings.IdleTimeout);
        }
        finally
        {
            File.Delete(modulePath);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Section_ReturnsEmptyDictionaryWhenSectionMissing()
    {
        var path = WriteTempSettingsFile("""{ "settings": {} }""");
        try
        {
            var section = Settings.Section("nonexistent", path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            var section = Settings.Section("mySection", path: path, localPath: NonExistentPath(), modulePath: NonExistentPath());

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
            Assert.ThrowsExactly<SettingsError>(() => Settings.Section("mySection", path: path, localPath: NonExistentPath(), modulePath: NonExistentPath()));
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
