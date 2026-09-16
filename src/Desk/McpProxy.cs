using System.Text;
using Croicu.Desk.Tools.Base;
using Microsoft.Win32.SafeHandles;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Bridges this process's own stdin/stdout to a registered MCP tool that <c>src/Service</c>'s
/// <c>Host</c> already launched and handed direct pipe access to (see docs/PROTOCOL.md's
/// <c>mcp</c> method, [issue #33](https://github.com/croicu/desk-tools/issues/33)) -- what makes
/// <c>desk mcp &lt;name&gt;</c> usable as a real <c>.mcp.json</c> <c>command</c> entry: whatever an
/// external MCP client (e.g. Claude Code) writes to this process's stdin gets forwarded to the
/// tool's own stdin through the duplicated handle, and whatever the tool writes to its stdout gets
/// forwarded back out this process's own stdout the same way. Desk itself never parses or
/// understands the MCP traffic -- purely a line relay, same "no SDK, hand-roll the transport"
/// approach the rest of this repo's MCP surface already takes.
/// </summary>
internal static class McpProxy
{
    // No BOM: same reasoning as Host.WriteEncoding/Client.WriteEncoding -- the tool's own stdin
    // reader expects plain UTF-8 JSON-RPC text with no preamble.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Opens the two duplicated pipe handles <see cref="Client.LaunchMcpTool"/> returned and pumps
    /// lines between them and <paramref name="input"/> (defaults to <see cref="Console.In"/>) /
    /// this process's own stdout (via <see cref="Logger.Print"/>, per CLAUDE.md's "Console.* is
    /// confined to src/Base/Sinks" rule -- <see cref="Program.Start"/> installs a silent sink before
    /// this runs, so nothing but these lines ever reaches real stdout). Blocks until our own stdin
    /// reaches EOF (the parent MCP client disconnected), then closes this process's copy of the
    /// tool's stdin -- so the tool sees EOF too and exits its own read loop gracefully, the same
    /// shutdown path a real client disconnect takes (see docs/PROTOCOL.md's Hello entry) -- and
    /// gives the tool's own output a bounded chance to finish draining before returning.
    /// </summary>
    internal static void Run(long stdinHandleValue, long stdoutHandleValue, TextReader? input = null)
    {
        Console.Out.NewLine = "\n";

        using var stdinHandle = new SafeFileHandle((nint)stdinHandleValue, ownsHandle: true);
        using var stdoutHandle = new SafeFileHandle((nint)stdoutHandleValue, ownsHandle: true);

        using var toolStdin = new StreamWriter(new FileStream(stdinHandle, FileAccess.Write), WriteEncoding) { NewLine = "\n", AutoFlush = true };
        using var toolStdout = new StreamReader(new FileStream(stdoutHandle, FileAccess.Read), Encoding.UTF8);

        var pumpFromTool = new Thread(() => PumpToolOutput(toolStdout)) { IsBackground = true };
        pumpFromTool.Start();

        PumpOurInput(input ?? Console.In, toolStdin);

        toolStdin.Close();
        pumpFromTool.Join(TimeSpan.FromSeconds(5));
    }

    private static void PumpOurInput(TextReader source, StreamWriter destination)
    {
        string? line;
        while ((line = source.ReadLine()) != null)
        {
            destination.WriteLine(line);
        }
    }

    private static void PumpToolOutput(StreamReader source)
    {
        string? line;
        while ((line = source.ReadLine()) != null)
        {
            Logger.Print(line);
        }
    }
}
