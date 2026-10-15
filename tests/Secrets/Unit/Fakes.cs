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
/// A stand-in for the dialog. Answers can be queued in advance (<see cref="Enqueue"/>: the next ask
/// completes at once, null = the user cancelled), or the test can leave an ask <i>open</i> and answer or
/// cancel it later by hand (<see cref="Answer"/>, <see cref="Fail"/>) -- like a person taking their time.
/// Honors the cancellation token the way the real dialog does (closing it completes the ask with null).
/// Keeps the arrays it hands out, so a test can check they were zeroed once dropped.
/// </summary>
internal sealed class FakePasswordPrompt : IPasswordPrompt
{
    private readonly Queue<string?> _answers = new();
    private readonly List<TaskCompletionSource<byte[]?>> _open = new();

    public List<(string DatabasePath, int Attempt, int MaxAttempts)> Asks { get; } = new();

    public List<byte[]> Handed { get; } = new();

    /// <summary>Set when the first ask arrives (so a test can wait for "the dialog is up").</summary>
    public ManualResetEventSlim Asked { get; } = new(initialState: false);

    /// <summary>True once the cancellation token of any ask has fired (the dialog was closed on shutdown).</summary>
    public bool WasCancelled { get; private set; }

    public void Enqueue(string? password)
    {
        lock (_answers)
        {
            _answers.Enqueue(password);
        }
    }

    public Task<byte[]?> AskAsync(string databasePath, int attempt, int maxAttempts, CancellationToken cancellation)
    {
        lock (Asks)
        {
            Asks.Add((databasePath, attempt, maxAttempts));
        }

        string? queued;
        var hasQueued = false;
        lock (_answers)
        {
            hasQueued = _answers.TryDequeue(out queued);
        }

        if (hasQueued)
        {
            Asked.Set();
            return Task.FromResult(Record(queued));
        }

        var open = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_open)
        {
            _open.Add(open);
        }

        cancellation.Register(CloseOnCancel, open);

        // Only now: a test that waits on Asked then calls Answer/Fail must find the dialog already open.
        Asked.Set();
        return open.Task;
    }

    /// <summary>The person types <paramref name="password"/> (or cancels with null) into the oldest open dialog.</summary>
    public void Answer(string? password)
    {
        TaskCompletionSource<byte[]?> target;
        lock (_open)
        {
            target = _open[0];
            _open.RemoveAt(0);
        }

        target.TrySetResult(Record(password));
    }

    /// <summary>The oldest open dialog could not be shown.</summary>
    public void Fail(Exception error)
    {
        TaskCompletionSource<byte[]?> target;
        lock (_open)
        {
            target = _open[0];
            _open.RemoveAt(0);
        }

        target.TrySetException(error);
    }

    private void CloseOnCancel(object? state)
    {
        WasCancelled = true;
        ((TaskCompletionSource<byte[]?>)state!).TrySetResult(null);
    }

    private byte[]? Record(string? password)
    {
        if (password is null)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(password);
        lock (Handed)
        {
            Handed.Add(bytes);
        }

        return bytes;
    }
}

/// <summary>Returns a queued answer (null = cancelled) and records how it was asked.</summary>
internal sealed class FakeFilePicker : IFilePicker
{
    private readonly string? _answer;

    public FakeFilePicker(string? answer)
    {
        _answer = answer;
    }

    public List<(string Title, string FilterName, string Extension)> Asks { get; } = new();

    public string? Pick(string title, string filterName, string extension)
    {
        Asks.Add((title, filterName, extension));
        return _answer;
    }
}

/// <summary>An in-memory <see cref="IDatabasePathStore"/> that counts writes.</summary>
internal sealed class FakeDatabasePathStore : IDatabasePathStore
{
    public FakeDatabasePathStore(string? saved)
    {
        Saved = saved;
    }

    public string? Saved { get; private set; }

    public int Writes { get; private set; }

    public string Description => "'secrets.databasePath' in the fake store";

    public string? Get() => Saved;

    public void Set(string path)
    {
        Saved = path;
        Writes++;
    }
}
