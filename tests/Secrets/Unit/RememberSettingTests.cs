using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// <c>secrets.rememberPassword</c>: on by default, <c>false</c> turns remembering the master password off entirely, and a
/// bad value or an unreadable section keeps the default (with a warning) instead of stopping anything.
/// </summary>
[TestClass]
public sealed class RememberSettingTests
{
    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private sealed class RecordingSink : DiagnosticsLog
    {
        public int Warnings { get; private set; }

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            if (level == TelemetryLevel.Warning)
            {
                Warnings++;
            }

            return base.Log(level, message, category);
        }
    }

    private static Dictionary<string, JsonElement> Section(string json)
    {
        var section = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            section[property.Name] = property.Value.Clone();
        }

        return section;
    }

    [TestMethod]
    public void IsEnabled_NothingConfigured_IsOn()
    {
        Assert.IsTrue(RememberSetting.IsEnabled(() => Section("{}")));
    }

    [TestMethod]
    public void IsEnabled_False_TurnsItOff()
    {
        Assert.IsFalse(RememberSetting.IsEnabled(() => Section("""{ "rememberPassword": false }""")));
    }

    [TestMethod]
    public void IsEnabled_True_IsOn()
    {
        Assert.IsTrue(RememberSetting.IsEnabled(() => Section("""{ "rememberPassword": true }""")));
    }

    [TestMethod]
    [DataRow("""{ "rememberPassword": "no" }""")]
    [DataRow("""{ "rememberPassword": 0 }""")]
    public void IsEnabled_NotABoolean_StaysOnAndWarns(string json)
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);

        Assert.IsTrue(RememberSetting.IsEnabled(() => Section(json)));
        Assert.AreEqual(1, sink.Warnings);
    }

    [TestMethod]
    public void IsEnabled_TheSectionCannotBeRead_StaysOnAndWarns()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);

        Dictionary<string, JsonElement> Unreadable() => throw new SettingsError("'secrets' must be a JSON object.");

        Assert.IsTrue(RememberSetting.IsEnabled(Unreadable));
        Assert.IsGreaterThanOrEqualTo(1, sink.Warnings);
    }
}
