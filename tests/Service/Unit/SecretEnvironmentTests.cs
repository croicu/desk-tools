using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Secrets;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Declared secrets -> environment variables at launch (issue #50, slice 4b). The rule under test is
/// the one from docs/ARCHITECTURE.md: Service is a convenience, never a gate, so every failure costs
/// only the variables it affects -- the tool still launches, with a warning that names what it went
/// without and never contains a value.
/// </summary>
[TestClass]
public sealed class SecretEnvironmentTests
{
    private const string SecretValue = "s3cr3t-value-123";

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

        public List<string> Messages(TelemetryLevel level)
        {
            var messages = new List<string>();
            foreach (var record in Received)
            {
                if (record.Level == level)
                {
                    messages.Add(record.Message);
                }
            }

            return messages;
        }

        public string Everything()
        {
            var all = new List<string>();
            foreach (var record in Received)
            {
                all.Add(record.Message);
            }

            return string.Join("\n", all);
        }
    }

    /// <summary>Answers per entry: key -> value, or throws what the test queued for that entry.</summary>
    private sealed class FakeSecretBroker : ISecretBroker
    {
        public Dictionary<string, Dictionary<string, string>> Entries { get; } = new();

        public Dictionary<string, Exception> Failures { get; } = new();

        public List<(string Entry, string[] Keys)> Reads { get; } = new();

        public IReadOnlyDictionary<string, string> Read(string entry, IReadOnlyList<string> keys)
        {
            Reads.Add((entry, keys.ToArray()));
            if (Failures.TryGetValue(entry, out var failure))
            {
                throw failure;
            }

            var result = new Dictionary<string, string>();
            if (Entries.TryGetValue(entry, out var known))
            {
                foreach (var key in keys)
                {
                    if (known.TryGetValue(key, out var value))
                    {
                        result[key] = value;
                    }
                }
            }

            return result;
        }

        public void Dispose()
        {
        }
    }

    private static McpToolLauncher.McpSecretDeclaration Declare(string env, string entry, string key) => new(env, entry, key);

    private static RecordingSink Capture()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        return sink;
    }

    [TestMethod]
    public void Resolve_NoDeclarations_ReturnsNothingAndNeverTouchesTheBroker()
    {
        var broker = new FakeSecretBroker();

        var env = SecretEnvironment.Resolve("tool", [], broker);

        Assert.IsEmpty(env);
        Assert.IsEmpty(broker.Reads);
    }

    [TestMethod]
    public void Resolve_Declared_AreDeliveredUnderTheirEnvironmentNames()
    {
        var broker = new FakeSecretBroker();
        broker.Entries["ntfy"] = new() { ["desk-pager"] = SecretValue };
        Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("NTFY_TOPIC", "ntfy", "desk-pager")], broker);

        Assert.HasCount(1, env);
        Assert.AreEqual(SecretValue, env["NTFY_TOPIC"]);
    }

    [TestMethod]
    public void Resolve_SeveralKeysOfOneEntry_AreReadWithOneCall()
    {
        var broker = new FakeSecretBroker();
        broker.Entries["ntfy"] = new() { ["a"] = "1", ["b"] = "2" };
        Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("A_VAR", "ntfy", "a"), Declare("B_VAR", "ntfy", "b")], broker);

        Assert.HasCount(1, broker.Reads);
        CollectionAssert.AreEqual(new[] { "a", "b" }, broker.Reads[0].Keys);
        Assert.AreEqual("1", env["A_VAR"]);
        Assert.AreEqual("2", env["B_VAR"]);
    }

    [TestMethod]
    public void Resolve_TwoVariablesNamingTheSameKey_AskForItOnce()
    {
        var broker = new FakeSecretBroker();
        broker.Entries["ntfy"] = new() { ["a"] = "1" };
        Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("ONE", "ntfy", "a"), Declare("TWO", "ntfy", "a")], broker);

        CollectionAssert.AreEqual(new[] { "a" }, broker.Reads[0].Keys);
        Assert.AreEqual("1", env["ONE"]);
        Assert.AreEqual("1", env["TWO"]);
    }

    [TestMethod]
    public void Resolve_OneEntryFails_TheOthersAreStillDelivered()
    {
        var broker = new FakeSecretBroker();
        broker.Failures["broken"] = new AppError("entry 'broken' not found.");
        broker.Entries["fine"] = new() { ["k"] = SecretValue };
        var sink = Capture();

        var env = SecretEnvironment.Resolve("tool", [Declare("BAD", "broken", "k"), Declare("GOOD", "fine", "k")], broker);

        Assert.HasCount(1, env);
        Assert.AreEqual(SecretValue, env["GOOD"]);
        var warnings = sink.Messages(TelemetryLevel.Warning);
        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "BAD");
        StringAssert.Contains(warnings[0], "broken");
        StringAssert.Contains(warnings[0], "tool");
    }

    [TestMethod]
    public void Resolve_BrokerThrowsSomethingUnexpected_StillDoesNotFailTheLaunch()
    {
        var broker = new FakeSecretBroker();
        broker.Failures["ntfy"] = new InvalidOperationException("boom");
        var sink = Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("NTFY_TOPIC", "ntfy", "desk-pager")], broker);

        Assert.IsEmpty(env);
        Assert.HasCount(1, sink.Messages(TelemetryLevel.Warning));
    }

    [TestMethod]
    public void Resolve_KeyMissingFromTheAnswer_SkipsThatVariableWithAWarning()
    {
        var broker = new FakeSecretBroker();
        broker.Entries["ntfy"] = new() { ["present"] = "1" };
        var sink = Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("HAVE", "ntfy", "present"), Declare("MISSING", "ntfy", "absent")], broker);

        Assert.HasCount(1, env);
        Assert.AreEqual("1", env["HAVE"]);
        var warnings = sink.Messages(TelemetryLevel.Warning);
        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "MISSING");
    }

    [TestMethod]
    public void Resolve_NoBroker_WarnsAndDeliversNothing()
    {
        var sink = Capture();

        var env = SecretEnvironment.Resolve("pager", [Declare("NTFY_TOPIC", "ntfy", "desk-pager")], broker: null);

        Assert.IsEmpty(env);
        Assert.HasCount(1, sink.Messages(TelemetryLevel.Warning));
    }

    [TestMethod]
    public void Resolve_NeverLogsAValue()
    {
        var broker = new FakeSecretBroker();
        broker.Entries["ntfy"] = new() { ["desk-pager"] = SecretValue };
        var sink = Capture();

        SecretEnvironment.Resolve("pager", [Declare("NTFY_TOPIC", "ntfy", "desk-pager"), Declare("MISSING", "ntfy", "absent")], broker);

        Assert.DoesNotContain(SecretValue, sink.Everything());
    }

    [TestMethod]
    public void Launch_DeliversResolvedSecretsInTheToolsEnvironment()
    {
        var registryDir = Path.Combine(Path.GetTempPath(), $"secret-launch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(registryDir, "mcp-registry"));
        try
        {
            // Prints the variable and exits -- a shell, since that is the one program every platform has.
            var (command, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", "[\"/c\", \"echo %PROBE_SECRET%\"]")
                : ("sh", "[\"-c\", \"echo $PROBE_SECRET\"]");
            var fragment = "{\"command\": \"" + command + "\", \"args\": " + args + ", \"env\": {}, "
                + "\"secrets\": [{\"env\": \"PROBE_SECRET\", \"entry\": \"ntfy\", \"key\": \"desk-pager\"}]}";
            File.WriteAllText(Path.Combine(registryDir, "mcp-registry", "probe.json"), fragment);
            var broker = new FakeSecretBroker();
            broker.Entries["ntfy"] = new() { ["desk-pager"] = SecretValue };
            Capture();

            using var tool = McpToolLauncher.Launch("probe", registryDir, secrets: broker);

            Assert.AreEqual(SecretValue, tool.StandardOutput.ReadLine()?.Trim());
        }
        finally
        {
            Directory.Delete(registryDir, recursive: true);
        }
    }
}
