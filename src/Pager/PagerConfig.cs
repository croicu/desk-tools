using System.Text.RegularExpressions;

namespace Croicu.Desk.Tools.Pager;

/// <summary>
/// What the pager needs to send a notification, read from its environment: <c>NTFY_TOPIC</c> (required;
/// delivered by Service from KeePass, or set by hand when the tool runs without Service) and
/// <c>NTFY_SERVER</c> (optional, default <see cref="DefaultServer"/>).
///
/// Never throws. A missing or unusable value leaves <see cref="Problem"/> set and the tool still starts:
/// only a <c>page</c> call answers "not configured" (docs/ARCHITECTURE.md, invariant 3). The problem text
/// says <i>what</i> is wrong with a variable but never repeats its value -- the topic is the one secret here.
/// </summary>
internal sealed partial class PagerConfig
{
    internal const string TopicVariable = "NTFY_TOPIC";
    internal const string ServerVariable = "NTFY_SERVER";
    internal const string DefaultServer = "https://ntfy.sh/";

    private PagerConfig(string? topic, Uri server, string? problem)
    {
        Topic = topic;
        Server = server;
        Problem = problem;
    }

    /// <summary>The topic to publish to, or null when <see cref="Problem"/> is set.</summary>
    internal string? Topic { get; }

    /// <summary>The ntfy server to publish to (always set: the default when <c>NTFY_SERVER</c> is not).</summary>
    internal Uri Server { get; }

    /// <summary>Why the pager cannot send, or null when it is configured. Safe to show: never contains a value.</summary>
    internal string? Problem { get; }

    internal bool IsConfigured => Problem is null;

    /// <summary>
    /// Reads the configuration through <paramref name="getEnvironmentVariable"/> (defaults to the real
    /// environment; a test passes its own).
    /// </summary>
    internal static PagerConfig FromEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        var read = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        var server = new Uri(DefaultServer);
        string? problem = null;

        var serverText = read(ServerVariable);
        if (!string.IsNullOrWhiteSpace(serverText))
        {
            if (TryParseServer(serverText.Trim(), out var parsed))
            {
                server = parsed;
            }
            else
            {
                problem = $"{ServerVariable} must be an absolute http or https URL.";
            }
        }

        var topicText = read(TopicVariable);
        if (string.IsNullOrWhiteSpace(topicText))
        {
            return new PagerConfig(null, server, $"{TopicVariable} is not set. Run through Service, the topic comes from KeePass entry 'ntfy', key 'desk-pager': Service could not read it, and its log (the 'secrets' category) says why -- e.g. the database path in the settings no longer exists, or the password prompt was cancelled. Run any other way, set {TopicVariable} in the tool's env in .mcp.json.");
        }

        var topic = topicText.Trim();
        if (!TopicPattern().IsMatch(topic))
        {
            return new PagerConfig(null, server, $"{TopicVariable} is not a valid ntfy topic (letters, digits, '_' and '-', 1 to 64 characters).");
        }

        return problem is null
            ? new PagerConfig(topic, server, null)
            : new PagerConfig(null, server, problem);
    }

    private static bool TryParseServer(string text, out Uri server)
    {
        // Publishing posts to this address; keep a trailing slash so a server mounted under a path
        // (https://host/ntfy/) is posted to as itself rather than to its parent.
        var normalized = text.EndsWith('/') ? text : text + "/";
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            server = parsed;
            return true;
        }

        server = new Uri(DefaultServer);
        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TopicPattern();
}
