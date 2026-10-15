using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Croicu.Desk.Tools.Service.Launcher;

/// <summary>
/// A launched MCP tool's standard input and output, as a pair of text streams: write lines to
/// <see cref="Input"/>, read lines from <see cref="Output"/>. What <see cref="Client.LaunchTool"/> returns once
/// <c>src/Service</c> has launched the tool and handed this process direct access to its pipes (see docs/PROTOCOL.md's
/// <c>mcp</c> method, [issue #32](https://github.com/croicu/desk-tools/issues/32)) -- after that Service is out of the
/// exchange entirely. Owns the two duplicated handles; disposing closes both, which is how the tool sees EOF on its
/// stdin and exits. The framing is the MCP stdio transport's: newline-delimited, UTF-8 without a byte-order mark.
///
/// Public constructor over any two streams, for hosts and tests that already have them; the handle-based factory is
/// internal because only <see cref="Client"/> receives raw handle values.
/// </summary>
public sealed class ToolConnection : IDisposable
{
    // No BOM: the tool expects plain UTF-8 newline-delimited JSON-RPC, and Encoding.UTF8 would prepend a 3-byte
    // preamble to the first thing written (see Client/Host.WriteEncoding for the same reasoning).
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public ToolConnection(TextWriter input, TextReader output)
    {
        Input = input;
        Output = output;
    }

    /// <summary>The tool's stdin: write one message per line (newline-terminated, flushed on every write). Close it to tell the tool its client is gone.</summary>
    public TextWriter Input { get; }

    /// <summary>The tool's stdout: read one message per line; <see langword="null"/> at the end of the tool's output.</summary>
    public TextReader Output { get; }

    internal static ToolConnection FromHandles(long stdinHandleValue, long stdoutHandleValue)
    {
        // Each handle is owned from here on: SafeFileHandle(ownsHandle: true) closes it with the stream.
        var stdinHandle = new SafeFileHandle((nint)stdinHandleValue, ownsHandle: true);
        var stdoutHandle = new SafeFileHandle((nint)stdoutHandleValue, ownsHandle: true);

        var input = new StreamWriter(new FileStream(stdinHandle, FileAccess.Write), WriteEncoding) { NewLine = "\n", AutoFlush = true };
        var output = new StreamReader(new FileStream(stdoutHandle, FileAccess.Read), Encoding.UTF8);
        return new ToolConnection(input, output);
    }

    public void Dispose()
    {
        // Closing stdin first: the tool's EOF is what makes it finish, and a broken pipe on a tool that has
        // already gone must not stop the output side from being released.
        try
        {
            Input.Dispose();
        }
        catch (IOException)
        {
            // The tool's stdin pipe broke because the tool is gone; nothing left to flush.
        }

        Output.Dispose();
    }
}
