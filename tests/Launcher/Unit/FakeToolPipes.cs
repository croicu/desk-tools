using System.IO.Pipes;
using System.Text;

namespace Croicu.Desk.Tools.Service.Launcher.Tests.Unit;

/// <summary>
/// Two anonymous pipes standing in for a launched tool's stdin and stdout. <see cref="StdinHandle"/> /
/// <see cref="StdoutHandle"/> are the raw handle values Service's <c>DuplicateHandle</c> handoff would give the
/// caller (the test's own process stands in for the caller), owned by whoever opens them from then on; the test
/// keeps the <i>other</i> end of each pipe, so it can read what reaches the tool's stdin
/// (<see cref="ToolStdin"/>), write the tool's output (<see cref="ToolStdout"/>) and "kill the tool" by closing its
/// end (<see cref="Die"/>).
/// </summary>
internal sealed class FakeToolPipes : IDisposable
{
    private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.In, HandleInheritability.None);
    private readonly AnonymousPipeServerStream _stdout = new(PipeDirection.Out, HandleInheritability.None);

    public FakeToolPipes()
    {
        StdinHandle = (long)_stdin.ClientSafePipeHandle.DangerousGetHandle();
        StdoutHandle = (long)_stdout.ClientSafePipeHandle.DangerousGetHandle();

        // Whoever opens those handle values owns them now; stop this side from also closing them.
        _stdin.ClientSafePipeHandle.SetHandleAsInvalid();
        _stdout.ClientSafePipeHandle.SetHandleAsInvalid();

        ToolStdin = new StreamReader(_stdin, Encoding.UTF8);
        ToolStdout = new StreamWriter(_stdout, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public long StdinHandle { get; }

    public long StdoutHandle { get; }

    public StreamReader ToolStdin { get; }

    public StreamWriter ToolStdout { get; }

    /// <summary>The tool exits: its write end of its own stdout closes, so the reader sees EOF.</summary>
    public void Die() => ToolStdout.Dispose();

    public void Dispose()
    {
        ToolStdin.Dispose();
        ToolStdout.Dispose();
    }
}
