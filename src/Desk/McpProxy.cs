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
    /// <summary>The client disconnected first (the normal way for a proxy to end).</summary>
    internal const int ExitClientDisconnected = 0;

    /// <summary>The tool went away first -- see <see cref="Run"/>.</summary>
    internal const int ExitToolGone = 1;

    private const string Category = "desk";

    // No BOM: same reasoning as Host.WriteEncoding/Client.WriteEncoding -- the tool's own stdin
    // reader expects plain UTF-8 JSON-RPC text with no preamble.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Opens the two duplicated pipe handles <see cref="Client.LaunchMcpTool"/> returned and pumps
    /// lines between them and <paramref name="input"/> (defaults to <see cref="Console.In"/>) /
    /// this process's own stdout (via <see cref="Logger.Print"/>, per CLAUDE.md's "Console.* is
    /// confined to src/Base/Sinks" rule -- <see cref="Program.Start"/> installs a silent sink before
    /// this runs, so nothing but these lines ever reaches real stdout).
    ///
    /// Returns as soon as <i>either</i> side ends, because a proxy is worthless without the other
    /// half (issue #54):
    /// <list type="bullet">
    /// <item>The client's input ended (the parent MCP client disconnected): closes this process's
    /// copy of the tool's stdin -- so the tool sees EOF too and exits its own read loop gracefully,
    /// the same shutdown path a real client disconnect takes (see docs/PROTOCOL.md's Hello entry) --
    /// gives the tool's own output a bounded chance to finish draining, and returns
    /// <see cref="ExitClientDisconnected"/>.</item>
    /// <item>The tool's output ended first (the tool exited, or Service went away and its watchdog
    /// reaped it): logs a warning and returns <see cref="ExitToolGone"/>, so the MCP client sees the
    /// server exit and can reconnect, instead of this process lingering forever.</item>
    /// </list>
    /// Both pumps run on background threads (reading <see cref="Console.In"/> can't be interrupted),
    /// so returning never waits on a read that may never complete.
    /// </summary>
    internal static int Run(long stdinHandleValue, long stdoutHandleValue, TextReader? input = null)
    {
        Console.Out.NewLine = "\n";

        using var stdinHandle = new SafeFileHandle((nint)stdinHandleValue, ownsHandle: true);
        using var stdoutHandle = new SafeFileHandle((nint)stdoutHandleValue, ownsHandle: true);

        using var toolStdin = new StreamWriter(new FileStream(stdinHandle, FileAccess.Write), WriteEncoding) { NewLine = "\n", AutoFlush = true };
        using var toolStdout = new StreamReader(new FileStream(stdoutHandle, FileAccess.Read), Encoding.UTF8);

        using var toolEnded = new ManualResetEventSlim(initialState: false);
        using var clientEnded = new ManualResetEventSlim(initialState: false);

        void PumpFromTool()
        {
            try
            {
                PumpToolOutput(toolStdout);
            }
            finally
            {
                toolEnded.Set();
            }
        }

        void PumpFromClient()
        {
            try
            {
                PumpOurInput(input ?? Console.In, toolStdin);
            }
            catch (IOException)
            {
                // The tool's stdin pipe broke because the tool is gone; PumpFromTool reports that.
            }
            finally
            {
                clientEnded.Set();
            }
        }

        new Thread(PumpFromTool) { IsBackground = true }.Start();
        new Thread(PumpFromClient) { IsBackground = true }.Start();

        WaitHandle.WaitAny([toolEnded.WaitHandle, clientEnded.WaitHandle]);

        // Checked in this order on purpose: a normal disconnect also makes the tool end (it sees EOF),
        // so "both set" must count as the client having left first, not as the tool vanishing.
        if (clientEnded.IsSet)
        {
            toolStdin.Close();
            toolEnded.Wait(DrainTimeout);
            return ExitClientDisconnected;
        }

        Logger.Warning("desk: the tool's output closed before the client disconnected; the tool is gone, exiting.", Category);
        return ExitToolGone;
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
