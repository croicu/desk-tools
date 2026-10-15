using System.Net;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Pager.Tests.Unit;

/// <summary>
/// Offline: the HTTP layer is a fake <see cref="HttpMessageHandler"/> (a third-party type, so a hand-written
/// fake rather than a mock of this project's own code). Log output is captured with a private sink so the
/// tests run in parallel and can assert the topic never reaches a log line.
/// </summary>
[TestClass]
public sealed class NotifierTests
{
    private const string Topic = "s3cret-topic-xyz";

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

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public Exception? Throw { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public List<(HttpMethod Method, Uri? Uri, string? ContentType, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, request.Content?.Headers.ContentType?.MediaType, body));

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return new HttpResponseMessage(Status);
        }
    }

    private static PagerConfig Configured(string? server = null)
    {
        string? Read(string name)
        {
            return name switch
            {
                PagerConfig.TopicVariable => Topic,
                PagerConfig.ServerVariable => server,
                _ => null,
            };
        }

        return PagerConfig.FromEnvironment(Read);
    }

    private static PageRequest Page(string message = "Build finished", string? title = null, int? priority = null, params string[] tags)
    {
        return new PageRequest(message, title, priority, tags);
    }

    [TestMethod]
    public void Send_PostsOneJsonDocumentToTheServerRoot()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured(), handler);

        var result = notifier.Send(Page());

        Assert.IsTrue(result.Sent);
        Assert.HasCount(1, handler.Requests);
        var request = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://ntfy.sh/", request.Uri!.ToString());
        Assert.AreEqual("application/json", request.ContentType);
    }

    [TestMethod]
    public void Send_PutsTheTopicInTheBodyNeverInTheAddress()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured(), handler);

        notifier.Send(Page());

        Assert.DoesNotContain(Topic, handler.Requests[0].Uri!.ToString());
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.AreEqual(Topic, doc.RootElement.GetProperty("topic").GetString());
        Assert.AreEqual("Build finished", doc.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    public void Send_OptionalFieldsAreOmittedUnlessGiven()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured(), handler);

        notifier.Send(Page());

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.IsFalse(doc.RootElement.TryGetProperty("title", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("priority", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("tags", out _));
    }

    [TestMethod]
    public void Send_TitlePriorityAndTagsAreCarried_AndUnicodeSurvives()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured(), handler);

        notifier.Send(Page("Déjà vu ✅", "Tâche terminée", 4, "white_check_mark", "warning"));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.AreEqual("Déjà vu ✅", doc.RootElement.GetProperty("message").GetString());
        Assert.AreEqual("Tâche terminée", doc.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(4, doc.RootElement.GetProperty("priority").GetInt32());
        Assert.AreEqual(2, doc.RootElement.GetProperty("tags").GetArrayLength());
    }

    [TestMethod]
    public void Send_CustomServer_IsPostedTo()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured("https://ntfy.example.com/relay"), handler);

        notifier.Send(Page());

        Assert.AreEqual("https://ntfy.example.com/relay/", handler.Requests[0].Uri!.ToString());
    }

    [TestMethod]
    public void Send_NotConfigured_SendsNothingAndSaysWhy()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(PagerConfig.FromEnvironment(_ => null), handler);

        var result = notifier.Send(Page());

        Assert.IsFalse(result.Sent);
        StringAssert.Contains(result.Text, "not configured");
        StringAssert.Contains(result.Text, "NTFY_TOPIC");
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public void Send_ServerRefuses_ReportsTheStatusWithoutThrowing(HttpStatusCode status)
    {
        var handler = new FakeHandler { Status = status };
        using var notifier = new Notifier(Configured(), handler);

        var result = notifier.Send(Page());

        Assert.IsFalse(result.Sent);
        StringAssert.Contains(result.Text, ((int)status).ToString());
        Assert.DoesNotContain(Topic, result.Text);
    }

    [TestMethod]
    public void Send_NetworkFailure_ReportsItWithoutThrowing()
    {
        var handler = new FakeHandler { Throw = new HttpRequestException("name resolution failed") };
        using var notifier = new Notifier(Configured(), handler);

        var result = notifier.Send(Page());

        Assert.IsFalse(result.Sent);
        StringAssert.Contains(result.Text, "Could not reach");
        StringAssert.Contains(result.Text, "ntfy.sh");
    }

    [TestMethod]
    public void Send_Timeout_ReportsItWithoutThrowing()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromSeconds(30) };
        using var notifier = new Notifier(Configured(), handler, timeout: TimeSpan.FromMilliseconds(100));

        var result = notifier.Send(Page());

        Assert.IsFalse(result.Sent);
        StringAssert.Contains(result.Text, "Timed out");
    }

    [TestMethod]
    public void Send_NeverLogsTheTopic()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var failing = new FakeHandler { Status = HttpStatusCode.Forbidden };
        var ok = new FakeHandler();
        var broken = new FakeHandler { Throw = new HttpRequestException("boom") };

        foreach (var handler in new[] { failing, ok, broken })
        {
            using var notifier = new Notifier(Configured(), handler);
            notifier.Send(Page());
        }

        Assert.IsNotEmpty(sink.Received);
        Assert.DoesNotContain(Topic, sink.Everything());
    }

    [TestMethod]
    public void Send_RequestBodyIsPlainUtf8WithoutABom()
    {
        var handler = new FakeHandler();
        using var notifier = new Notifier(Configured(), handler);

        notifier.Send(Page("x"));

        Assert.AreNotEqual('﻿', handler.Requests[0].Body[0]);
        Assert.AreEqual('{', handler.Requests[0].Body[0]);
        Assert.IsNotNull(Encoding.UTF8);
    }
}
