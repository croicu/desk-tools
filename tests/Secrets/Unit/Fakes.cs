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
