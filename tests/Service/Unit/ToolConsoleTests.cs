using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Mocks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Whether a launched tool gets a visible console window (issue #44): hidden unless <c>settings.console</c> or a
/// per-tool <c>"tools"</c> override says otherwise. The settings files are not read here: every test hands
/// <see cref="ToolConsole.IsVisible"/> the <c>"tools"</c> section it should see.
/// </summary>
[TestClass]
public sealed class ToolConsoleTests
{
    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private sealed class RecordingSink : DiagnosticsLog
    {
        public List<TelemetryRecord> Received { get; } = new();

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            var record = base.Log(level, message, category);
            Received.Add(record);
            return record;
        }

        public int Warnings()
        {
            var count = 0;
            foreach (var record in Received)
            {
                if (record.Level == TelemetryLevel.Warning)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private static Dictionary<string, JsonElement> Tools(string json)
    {
        var tools = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            tools[property.Name] = property.Value.Clone();
        }

        return tools;
    }

    private static bool Visible(string tool, bool global, string toolsJson)
    {
        return ToolConsole.IsVisible(tool, new TestSettings { ToolConsole = global }, () => Tools(toolsJson));
    }

    [TestMethod]
    public void IsVisible_NothingConfigured_IsHidden()
    {
        Assert.IsFalse(Visible("hello", global: false, "{}"));
    }

    [TestMethod]
    public void IsVisible_GlobalOn_ShowsEveryTool()
    {
        Assert.IsTrue(Visible("hello", global: true, "{}"));
        Assert.IsTrue(Visible("pager", global: true, "{}"));
    }

    [TestMethod]
    public void IsVisible_PerToolOverrideOn_ShowsOnlyThatTool()
    {
        const string tools = """{ "hello": { "console": true } }""";

        Assert.IsTrue(Visible("hello", global: false, tools));
        Assert.IsFalse(Visible("pager", global: false, tools));
    }

    [TestMethod]
    public void IsVisible_PerToolOverrideOff_HidesThatToolEvenWhenGlobalIsOn()
    {
        const string tools = """{ "hello": { "console": false } }""";

        Assert.IsFalse(Visible("hello", global: true, tools));
        Assert.IsTrue(Visible("pager", global: true, tools));
    }

    [TestMethod]
    public void IsVisible_ToolNameComparisonIgnoresCase()
    {
        Assert.IsTrue(Visible("Hello", global: false, """{ "hello": { "console": true } }"""));
    }

    [TestMethod]
    public void IsVisible_OverrideWithoutAConsoleKey_FallsBackToTheGlobalDefault()
    {
        Assert.IsTrue(Visible("hello", global: true, """{ "hello": { "other": 1 } }"""));
        Assert.IsFalse(Visible("hello", global: false, """{ "hello": { } }"""));
    }

    [TestMethod]
    [DataRow("""{ "hello": true }""")]
    [DataRow("""{ "hello": "yes" }""")]
    [DataRow("""{ "hello": { "console": "yes" } }""")]
    [DataRow("""{ "hello": { "console": 1 } }""")]
    public void IsVisible_MalformedOverride_WarnsAndUsesTheDefault_NeverFails(string tools)
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);

        Assert.IsFalse(Visible("hello", global: false, tools));
        Assert.IsTrue(Visible("hello", global: true, tools));
        Assert.AreEqual(2, sink.Warnings());
    }

    [TestMethod]
    public void IsVisible_TheToolsSectionCannotBeRead_WarnsAndUsesTheDefault()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);

        Dictionary<string, JsonElement> Unreadable() => throw new SettingsError("'tools' in 'settings.json' must be a JSON object.");

        var visible = ToolConsole.IsVisible("hello", new TestSettings { ToolConsole = true }, Unreadable);

        Assert.IsTrue(visible);

        // At least ours (creating a SettingsError itself already logs one, so the exact count is not the point).
        Assert.IsGreaterThanOrEqualTo(1, sink.Warnings());
    }
}
