namespace Croicu.Desk.Tools.Pager.Tests.Unit;

/// <summary>
/// <see cref="PagerConfig"/> never throws: a missing or unusable variable leaves a problem that a
/// <c>page</c> call reports, and the problem text never repeats a value (the topic is the secret).
/// </summary>
[TestClass]
public sealed class PagerConfigTests
{
    private static PagerConfig From(string? topic, string? server = null)
    {
        string? Read(string name)
        {
            return name switch
            {
                PagerConfig.TopicVariable => topic,
                PagerConfig.ServerVariable => server,
                _ => null,
            };
        }

        return PagerConfig.FromEnvironment(Read);
    }

    [TestMethod]
    public void FromEnvironment_TopicOnly_UsesTheDefaultServer()
    {
        var config = From("my-topic_1");

        Assert.IsTrue(config.IsConfigured);
        Assert.AreEqual("my-topic_1", config.Topic);
        Assert.AreEqual("https://ntfy.sh/", config.Server.ToString());
        Assert.IsNull(config.Problem);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void FromEnvironment_NoTopic_IsNotConfiguredAndSaysWhich(string? topic)
    {
        var config = From(topic);

        Assert.IsFalse(config.IsConfigured);
        Assert.IsNull(config.Topic);
        StringAssert.Contains(config.Problem, "NTFY_TOPIC");
    }

    [TestMethod]
    public void FromEnvironment_NoTopic_PointsAtServicesLogForTheReason()
    {
        var problem = From(null).Problem!;

        StringAssert.Contains(problem, "Service");
        StringAssert.Contains(problem, "log");
        StringAssert.Contains(problem, ".mcp.json");
    }

    [TestMethod]
    [DataRow("has space")]
    [DataRow("slash/inside")]
    [DataRow("question?mark")]
    [DataRow("ünïcode")]
    public void FromEnvironment_InvalidTopic_IsNotConfiguredAndNeverEchoesIt(string topic)
    {
        var config = From(topic);

        Assert.IsFalse(config.IsConfigured);
        Assert.DoesNotContain(topic, config.Problem!);
    }

    [TestMethod]
    public void FromEnvironment_TopicLongerThan64_IsNotConfigured()
    {
        Assert.IsFalse(From(new string('a', 65)).IsConfigured);
        Assert.IsTrue(From(new string('a', 64)).IsConfigured);
    }

    [TestMethod]
    public void FromEnvironment_TopicIsTrimmed()
    {
        Assert.AreEqual("abc", From("  abc \n").Topic);
    }

    [TestMethod]
    public void FromEnvironment_CustomServer_IsUsedAndGetsATrailingSlash()
    {
        var config = From("abc", "https://ntfy.example.com/relay");

        Assert.IsTrue(config.IsConfigured);
        Assert.AreEqual("https://ntfy.example.com/relay/", config.Server.ToString());
    }

    [TestMethod]
    [DataRow("not a url")]
    [DataRow("ftp://ntfy.example.com")]
    [DataRow("//ntfy.example.com")]
    public void FromEnvironment_UnusableServer_IsNotConfigured(string server)
    {
        var config = From("abc", server);

        Assert.IsFalse(config.IsConfigured);
        StringAssert.Contains(config.Problem, "NTFY_SERVER");
    }

    [TestMethod]
    public void FromEnvironment_PlainHttpServer_IsAllowed()
    {
        Assert.IsTrue(From("abc", "http://localhost:8080").IsConfigured);
    }
}
