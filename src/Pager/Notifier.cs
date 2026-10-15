using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Pager;

/// <summary>One notification to send. <see cref="Priority"/> is ntfy's 1 (min) to 5 (max) scale; null means the default (3).</summary>
internal sealed record PageRequest(string Message, string? Title, int? Priority, IReadOnlyList<string> Tags);

/// <summary>Whether the notification went out, and a sentence saying what happened (safe to show: never contains the topic).</summary>
internal sealed record PageResult(bool Sent, string Text);

/// <summary>
/// Publishes a notification to ntfy with its JSON API: one POST of <c>{"topic", "message", "title"?,
/// "priority"?, "tags"?}</c> to the server's root. Compared with the usual <c>POST /&lt;topic&gt;</c> this keeps the
/// topic -- the one secret here -- out of the URL (so out of proxy and server access logs and of any
/// exception message that names the address), and carries a non-ASCII title or message as plain UTF-8
/// instead of squeezing it through HTTP headers.
///
/// Never throws for a failed send: a network error, a timeout or a non-success answer becomes a
/// <see cref="PageResult"/> with <see cref="PageResult.Sent"/> false, because an agent calling the tool should read
/// what went wrong rather than see the server fail. The HTTP layer is injectable (<paramref name="handler"/>) so
/// tests stay offline.
/// </summary>
internal sealed class Notifier : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string Category = "pager";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PagerConfig _config;
    private readonly HttpClient _http;

    internal Notifier(PagerConfig config, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _config = config;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = timeout ?? DefaultTimeout,
        };
    }

    internal PageResult Send(PageRequest request)
    {
        if (!_config.IsConfigured)
        {
            Logger.Warning($"pager: not configured: {_config.Problem}", Category);
            return new PageResult(false, $"The pager is not configured: {_config.Problem}");
        }

        var body = new Payload(_config.Topic!, request.Message, request.Title, request.Priority, request.Tags.Count == 0 ? null : request.Tags);
        using var content = new StringContent(JsonSerializer.Serialize(body, SerializerOptions), new UTF8Encoding(false), new MediaTypeHeaderValue("application/json"));

        var host = _config.Server.Host;
        try
        {
            Logger.Diagnostic($"pager: publishing to '{host}' ({request.Message.Length} character message).", Category);
            using var response = _http.PostAsync(_config.Server, content).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                Logger.Info($"pager: notification sent via '{host}'.", Category);
                return new PageResult(true, "Notification sent.");
            }

            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            Logger.Warning($"pager: '{host}' answered {status}.", Category);
            return new PageResult(false, $"The ntfy server '{host}' refused the notification: {status}.");
        }
        catch (TaskCanceledException)
        {
            Logger.Warning($"pager: publishing to '{host}' timed out.", Category);
            return new PageResult(false, $"Timed out after {(int)_http.Timeout.TotalSeconds} seconds waiting for the ntfy server '{host}'.");
        }
        catch (HttpRequestException error)
        {
            Logger.Warning($"pager: could not reach '{host}': {error.Message}", Category);
            return new PageResult(false, $"Could not reach the ntfy server '{host}': {error.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private sealed record Payload(string Topic, string Message, string? Title, int? Priority, IReadOnlyList<string>? Tags);
}
