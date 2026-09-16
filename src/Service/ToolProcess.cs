using System.Diagnostics;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// A running child process launched by <see cref="ToolLauncher"/>, with its standard input/output
/// already redirected. Thin wrapper over <see cref="Process"/> -- exposes only what issue #30's
/// scope needs (write requests, read responses, tear down); no MCP framing/dispatch of its own,
/// that's deferred to whatever eventually drives this (see <see cref="McpToolLauncher"/>'s own
/// remarks).
/// </summary>
internal sealed class ToolProcess : IDisposable
{
    private readonly Process _process;

    internal ToolProcess(Process process)
    {
        _process = process;
    }

    /// <summary>Writes requests to the tool's redirected stdin.</summary>
    internal StreamWriter StandardInput => _process.StandardInput;

    /// <summary>Reads responses from the tool's redirected stdout.</summary>
    internal StreamReader StandardOutput => _process.StandardOutput;

    /// <summary>True while the underlying process is still running.</summary>
    internal bool HasExited => _process.HasExited;

    /// <summary>
    /// Releases Service's own OS handle to the process wrapper, without waiting for it to exit or
    /// touching its stdio -- for use once <see cref="HandleDuplicator"/> has already handed both
    /// stdio pipe ends off to another process (issue #32) and closed Service's own stdin copy.
    /// Unlike <see cref="Dispose"/>, this must never block on <c>WaitForExit</c>: the tool is meant
    /// to keep running for as long as its new owner needs it, not exit promptly. Doesn't kill or
    /// otherwise affect the actual OS process -- only releases the <see cref="Process"/> wrapper's
    /// own resources, the same way letting a <see cref="Process"/> object go ungarbage-collected
    /// without ever calling <see cref="Dispose"/> already wouldn't kill it either.
    /// </summary>
    internal void DisownAfterHandoff()
    {
        _process.Dispose();
    }

    /// <summary>
    /// Closes stdin (so a well-behaved tool sees EOF and exits its own read loop, the same shutdown
    /// path a real client disconnect takes -- see docs/PROTOCOL.md's Hello entry) and waits briefly
    /// for the process to exit on its own before disposing it.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                _process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
            // Process never started or already exited -- nothing to close.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
