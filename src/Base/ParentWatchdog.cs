using System.Diagnostics;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Makes a launched tool exit once the process that launched it does (issue #45). Service sets
/// <see cref="EnvironmentVariable"/> to its own PID in every tool it spawns (see
/// <c>ToolLauncher.Launch</c>); the tool calls <see cref="Start"/> as early as it can, and a
/// background thread waits on that PID, then exits the tool. A tool launched by anything other than
/// Service (no variable set, or an unparseable/already-dead PID) gets no watchdog and runs as
/// before.
///
/// Cross-platform (Architecture convention 11): <see cref="Process.GetProcessById(int)"/> and
/// <see cref="Process.WaitForExit()"/> work on every platform .NET runs on. The Python tools
/// duplicate this logic inline instead (scripts/ are intentionally self-contained).
/// </summary>
public static class ParentWatchdog
{
    /// <summary>Name of the environment variable carrying the launching process's PID.</summary>
    public const string EnvironmentVariable = "DESK_PARENT_PID";

    private const string Category = "watchdog";

    /// <summary>
    /// Starts the watchdog if <see cref="EnvironmentVariable"/> names a live process. Returns the
    /// background thread (null if no watchdog was started) so a test can join it.
    /// <paramref name="exit"/> is the testing seam for what "exit the tool" means (defaults to
    /// <see cref="Environment.Exit(int)"/>), and <paramref name="parentPid"/> overrides the
    /// environment lookup.
    /// </summary>
    public static Thread? Start(Action<int>? exit = null, string? parentPid = null)
    {
        var raw = parentPid ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, out var pid))
        {
            Logger.Warning($"watchdog: ignoring unparseable {EnvironmentVariable} '{raw}'.", Category);
            return null;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // The parent was already gone by the time this tool got far enough to start the
            // watchdog -- the same condition the watchdog exists to act on, so act on it now.
            Logger.Info($"watchdog: parent process {pid} is already gone; exiting.", Category);
            (exit ?? Environment.Exit)(0);
            return null;
        }

        var exitAction = exit ?? Environment.Exit;

        void Watch()
        {
            using (parent)
            {
                parent.WaitForExit();
            }

            Logger.Info($"watchdog: parent process {pid} exited; exiting.", Category);
            exitAction(0);
        }

        var thread = new Thread(Watch) { IsBackground = true, Name = "parent-watchdog" };
        thread.Start();
        Logger.Diagnostic($"watchdog: watching parent process {pid}.", Category);
        return thread;
    }
}
