using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// The optional <c>secrets</c> array of a registry fragment (issue #50, slice 4a). The rule under test
/// is that the manifest is optional and Service is a convenience, never a gate: a bad declaration costs
/// only that one capability -- it is skipped with a warning and the tool still launches. Every test
/// captures log output with its own sink, so they run in parallel without touching the console.
/// </summary>
[TestClass]
public sealed class McpSecretDeclarationTests
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

        public List<string> Warnings()
        {
            var warnings = new List<string>();
            foreach (var record in Received)
            {
                if (record.Level == TelemetryLevel.Warning)
                {
                    warnings.Add(record.Message);
                }
            }

            return warnings;
        }
    }

    private static string Fragment(string secrets, string env = "{}")
    {
        var secretsField = secrets.Length == 0 ? string.Empty : $", \"secrets\": {secrets}";
        return $$"""{"name": "t", "type": "stdio", "command": "dotnet", "args": ["T.dll"], "env": {{env}}{{secretsField}}}""";
    }

    private static McpToolLauncher.McpToolFragment Parse(string json, out RecordingSink sink)
    {
        sink = new RecordingSink();
        Logger.SetLogger(sink);
        return McpToolLauncher.ParseFragment(json);
    }

    [TestMethod]
    public void ParseFragment_NoSecretsField_MeansNone()
    {
        var fragment = Parse(Fragment(string.Empty), out var sink);

        Assert.IsEmpty(fragment.Secrets);
        Assert.IsEmpty(sink.Warnings());
    }

    [TestMethod]
    public void ParseFragment_ValidDeclarations_AreReadInOrder()
    {
        var fragment = Parse(Fragment("""[{"env": "NTFY_TOPIC", "entry": "ntfy", "key": "desk-pager"}, {"env": "OTHER_1", "entry": "Group/Sub", "key": "k2"}]"""), out var sink);

        Assert.HasCount(2, fragment.Secrets);
        Assert.AreEqual(new McpToolLauncher.McpSecretDeclaration("NTFY_TOPIC", "ntfy", "desk-pager"), fragment.Secrets[0]);
        Assert.AreEqual(new McpToolLauncher.McpSecretDeclaration("OTHER_1", "Group/Sub", "k2"), fragment.Secrets[1]);
        Assert.IsEmpty(sink.Warnings());
    }

    [TestMethod]
    public void ParseFragment_EmptySecretsArray_MeansNone()
    {
        Assert.IsEmpty(Parse(Fragment("[]"), out _).Secrets);
    }

    [TestMethod]
    public void ParseFragment_SecretsNotAnArray_IsIgnoredWithAWarningAndTheToolStillParses()
    {
        var fragment = Parse(Fragment("\"oops\""), out var sink);

        Assert.AreEqual("dotnet", fragment.Command);
        Assert.IsEmpty(fragment.Secrets);
        Assert.HasCount(1, sink.Warnings());
        Assert.Contains("'secrets' field must be an array", sink.Warnings()[0]);
    }

    [TestMethod]
    public void ParseFragment_OneBadDeclaration_IsSkippedAndTheGoodOnesSurvive()
    {
        var fragment = Parse(Fragment("""[{"env": "GOOD_ONE", "entry": "a", "key": "b"}, {"env": "BAD NAME", "entry": "a", "key": "b"}, {"env": "GOOD_TWO", "entry": "c", "key": "d"}]"""), out var sink);

        Assert.HasCount(2, fragment.Secrets);
        Assert.AreEqual("GOOD_ONE", fragment.Secrets[0].Env);
        Assert.AreEqual("GOOD_TWO", fragment.Secrets[1].Env);
        Assert.HasCount(1, sink.Warnings());
        Assert.Contains("secrets[1]", sink.Warnings()[0]);
        Assert.Contains("'BAD NAME'", sink.Warnings()[0]);
        Assert.Contains("launching the tool without it", sink.Warnings()[0]);
    }

    [TestMethod]
    [DataRow("""[5]""", "must be an object")]
    [DataRow("""[{"entry": "a", "key": "b"}]""", "missing a non-empty string")]
    [DataRow("""[{"env": "X", "key": "b"}]""", "missing a non-empty string")]
    [DataRow("""[{"env": "X", "entry": "a"}]""", "missing a non-empty string")]
    [DataRow("""[{"env": "X", "entry": "", "key": "b"}]""", "missing a non-empty string")]
    [DataRow("""[{"env": "X", "entry": "a", "key": 7}]""", "missing a non-empty string")]
    [DataRow("""[{"env": "1STARTS_WITH_DIGIT", "entry": "a", "key": "b"}]""", "invalid environment variable name")]
    [DataRow("""[{"env": "HAS-DASH", "entry": "a", "key": "b"}]""", "invalid environment variable name")]
    [DataRow("""[{"env": "DESK_PARENT_PID", "entry": "a", "key": "b"}]""", "sets that variable itself")]
    [DataRow("""[{"env": "desk_parent_pid", "entry": "a", "key": "b"}]""", "sets that variable itself")]
    public void ParseFragment_BadDeclaration_NeverThrowsAndIsSkippedWithAWarning(string secrets, string expectedReason)
    {
        var fragment = Parse(Fragment(secrets), out var sink);

        Assert.AreEqual("dotnet", fragment.Command, "the tool must still be launchable.");
        Assert.IsEmpty(fragment.Secrets);
        Assert.HasCount(1, sink.Warnings());
        Assert.Contains(expectedReason, sink.Warnings()[0]);
    }

    [TestMethod]
    public void ParseFragment_DuplicateEnvName_KeepsTheFirstAndWarnsAboutTheRepeat_CaseInsensitively()
    {
        var fragment = Parse(Fragment("""[{"env": "TOPIC", "entry": "a", "key": "b"}, {"env": "topic", "entry": "c", "key": "d"}]"""), out var sink);

        Assert.HasCount(1, fragment.Secrets);
        Assert.AreEqual("a", fragment.Secrets[0].Entry);
        Assert.Contains("more than once", sink.Warnings()[0]);
    }

    [TestMethod]
    public void ParseFragment_EnvAlreadySetByTheFragment_TheExplicitValueWins()
    {
        var fragment = Parse(Fragment("""[{"env": "TOPIC", "entry": "a", "key": "b"}]""", env: """{"topic": "explicit"}"""), out var sink);

        Assert.IsEmpty(fragment.Secrets);
        Assert.AreEqual("explicit", fragment.Env["topic"]);
        Assert.Contains("only one source", sink.Warnings()[0]);
    }

    [TestMethod]
    public void ParseFragment_NoCommand_StillThrows_BecauseThereIsNothingToLaunch()
    {
        // Contrast with the secrets rules above: a fragment Service cannot launch at all is an error.
        Assert.ThrowsExactly<AppError>(() => McpToolLauncher.ParseFragment("""{"name": "t", "args": []}"""));
    }
}
