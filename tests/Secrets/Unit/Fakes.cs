using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>Records every call and replays queued results, in order.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _results = new();

    public List<(string FileName, string[] Args, byte[] Stdin)> Calls { get; } = new();

    public void Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _results.Enqueue(new ProcessResult(exitCode, Encoding.UTF8.GetBytes(stdout), stderr));
    }

    public ProcessResult Run(string fileName, IReadOnlyList<string> args, ReadOnlySpan<byte> stdin)
    {
        Calls.Add((fileName, args.ToArray(), stdin.ToArray()));
        return _results.Dequeue();
    }
}

/// <summary>Hands out a fixed list of passwords, one per attempt, then null (cancel).</summary>
internal sealed class FakePasswordSource : IPasswordSource
{
    private readonly string[] _passwords;

    public FakePasswordSource(params string[] passwords)
    {
        _passwords = passwords;
    }

    public List<byte[]> Handed { get; } = new();

    public List<int> Attempts { get; } = new();

    public int RejectedCount { get; private set; }

    public byte[]? Get(string databasePath, int attempt)
    {
        Attempts.Add(attempt);
        if (attempt >= _passwords.Length)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(_passwords[attempt]);
        Handed.Add(bytes);
        return bytes;
    }

    public void Rejected()
    {
        RejectedCount++;
    }
}

/// <summary>
/// Replays queued password entries (null = the user cancelled) and records each ask. Keeps the arrays
/// it hands out, so a test can check they were zeroed once dropped.
/// </summary>
internal sealed class FakePasswordPrompt : IPasswordPrompt
{
    private readonly Queue<string?> _answers = new();

    public List<(string DatabasePath, int Attempt, int MaxAttempts)> Asks { get; } = new();

    public List<byte[]> Handed { get; } = new();

    /// <summary>When set, <see cref="Ask"/> blocks on it after recording the ask (to hold a dialog "open").</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public ManualResetEventSlim Asked { get; } = new(initialState: false);

    public void Enqueue(string? password)
    {
        _answers.Enqueue(password);
    }

    public byte[]? Ask(string databasePath, int attempt, int maxAttempts)
    {
        lock (Asks)
        {
            Asks.Add((databasePath, attempt, maxAttempts));
        }

        Asked.Set();
        Gate?.Wait();

        string? answer;
        lock (_answers)
        {
            answer = _answers.Dequeue();
        }

        if (answer is null)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(answer);
        lock (Handed)
        {
            Handed.Add(bytes);
        }

        return bytes;
    }
}
