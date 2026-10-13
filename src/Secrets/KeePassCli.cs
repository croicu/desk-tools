using System.Security.Cryptography;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Reads one entry's Notes by running <c>keepassxc-cli show -q -a Notes &lt;db&gt; &lt;entry&gt;</c>
/// with the master password as one line on stdin (never on the command line). The CLI is used
/// instead of a .NET KDBX library because the user already trusts it, updates it with the app, and
/// the .kdbx is medium-writable input an elevated process shouldn't hand to a young parser (see
/// issue #50).
///
/// Behavior checked live against keepassxc-cli 2.7.12: <c>-q</c> suppresses the "Enter password"
/// prompt; a wrong password exits 1 with an <b>empty</b> stderr under <c>-q</c>, while a missing
/// entry exits 1 with a message on stderr. So exit 1 + empty stderr is read as an authentication
/// failure (re-prompt) and anything else non-zero as a plain failure -- rather than matching message
/// text, which may be localized.
/// </summary>
internal sealed class KeePassCli
{
    internal const int SummaryLimit = 200;

    private readonly IProcessRunner _runner;
    private readonly string _exePath;

    /// <summary>
    /// <paramref name="exePath"/> must already be the trusted absolute path to
    /// <c>keepassxc-cli</c> -- see <see cref="CliLocation"/>, which is what production uses to get it.
    /// </summary>
    internal KeePassCli(IProcessRunner runner, string exePath)
    {
        _runner = runner;
        _exePath = exePath;
    }

    /// <summary>
    /// Reads <paramref name="entry"/>'s Notes from <paramref name="databasePath"/>. Never throws for
    /// a bad password or a missing entry -- those come back as <see cref="ReadStatus"/> values so the
    /// caller can re-prompt on the first and fail cleanly on the second. <paramref name="password"/>
    /// is copied into a pinned stdin buffer (plus the newline the CLI reads up to) that is zeroed
    /// before returning; zeroing the caller's own copy stays the caller's job.
    /// </summary>
    internal NotesRead ReadNotes(string databasePath, string entry, ReadOnlySpan<byte> password)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.StartsWith('-'))
        {
            throw new AppError($"invalid KeePass entry name '{entry}': must be non-empty and not start with '-'.", LogCategory.Secrets);
        }

        var stdin = GC.AllocateArray<byte>(password.Length + 1, pinned: true);
        ProcessResult result;
        try
        {
            password.CopyTo(stdin);
            stdin[password.Length] = (byte)'\n';

            result = _runner.Run(_exePath, ["show", "-q", "-a", "Notes", databasePath, entry], stdin);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stdin);
        }

        if (result.ExitCode == 0)
        {
            return new NotesRead(ReadStatus.Ok, result.Stdout, string.Empty);
        }

        CryptographicOperations.ZeroMemory(result.Stdout);

        var detail = Summarize(result.ExitCode, result.Stderr);
        var status = string.IsNullOrWhiteSpace(result.Stderr) ? ReadStatus.AuthenticationFailed : ReadStatus.Failed;
        return new NotesRead(status, [], detail);
    }

    /// <summary>
    /// Exit code plus the first line of stderr, truncated -- enough to diagnose, never the whole raw
    /// output (see issue #50's logging rule).
    /// </summary>
    internal static string Summarize(int exitCode, string stderr)
    {
        var firstLine = stderr.Split('\n', 2)[0].Trim();
        if (firstLine.Length > SummaryLimit)
        {
            firstLine = firstLine[..SummaryLimit] + "...";
        }

        return firstLine.Length == 0 ? $"exit code {exitCode}" : $"exit code {exitCode}: {firstLine}";
    }
}
