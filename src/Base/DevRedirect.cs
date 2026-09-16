using System.Diagnostics;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Lets an installed, elevated exe (Service.exe/Desk.exe under Program Files) hand off to a dev
/// build without ever touching its own install folder -- the thing that made
/// scripts/link-program-files.py's original rename-the-whole-folder approach fight Windows the
/// whole way (a running process's own cwd/image blocking exactly the rename it needed to do, see
/// that script's own git history). Instead: if a ".dev" junction exists right next to the running
/// exe (created by link-program-files.py, pointing at this repo's own out/&lt;Configuration&gt;/
/// net10.0/[&lt;RID&gt;/] build output), <see cref="TryHandoff"/> re-spawns the identically-named exe
/// from inside that junction, inheriting this process's own stdio untouched (critical for
/// <c>desk mcp &lt;name&gt;</c>, whose whole job is proxying an MCP host's own stdin/stdout), waits for
/// it, and returns its exit code -- the caller relays that and returns immediately, never running
/// its own real startup logic at all. Creating/removing ".dev" itself is a plain subfolder
/// junction inside Program Files, not a rename of the folder a running process lives in -- no
/// self-referential lock, unlike the old approach.
/// </summary>
public static class DevRedirect
{
    private const string DevDirName = ".dev";

    /// <summary>
    /// Must be called as the very first thing <c>Main</c> does, before any settings/logging setup
    /// -- a dev handoff should do zero work of its own beyond relaying to the real dev process.
    /// <paramref name="exeName"/> is this app's own executable file name (e.g. <c>"Service.exe"</c>),
    /// looked up inside <c>.dev</c> by that same name. Returns <see langword="null"/> when there's
    /// no <c>.dev</c> redirect (normal startup should proceed); otherwise the dev process' own exit
    /// code, which the caller should return from <c>Main</c> immediately.
    /// </summary>
    public static int? TryHandoff(string exeName, string[] args)
    {
        var devDir = Path.Combine(AppContext.BaseDirectory, DevDirName);
        if (!Directory.Exists(devDir))
        {
            return null;
        }

        // Defense in depth against a self-referential ".dev" (confirmed the hard way -- see
        // scripts/link-program-files.py's own remarks on how a misconfigured target produces
        // exactly this): without this check, a ".dev" that resolves back to this same directory
        // would make this method hand off to itself, which does the same check again, spawning
        // real nested processes indefinitely (bounded only by Windows' own path-length limit in
        // practice, not by anything this code does). ResolveLinkTarget(returnFinalTarget: true)
        // follows the junction to its real, canonical destination rather than trusting the
        // unresolved path string.
        var resolvedDevDir = Directory.ResolveLinkTarget(devDir, returnFinalTarget: true)?.FullName ?? devDir;
        if (string.Equals(Path.TrimEndingDirectorySeparator(resolvedDevDir), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new AppError($"'{devDir}' resolves back to '{AppContext.BaseDirectory}' itself -- refusing a self-referential dev redirect.");
        }

        var devExePath = Path.Combine(devDir, exeName);
        if (!File.Exists(devExePath))
        {
            return null;
        }

        // Deliberately no WorkingDirectory override -- inherits whatever cwd this (installed)
        // process itself already has (e.g. System32 for Service, via the scheduled task's own
        // default). Setting it to devDir here would give the respawned dev process's own cwd an
        // implicit Windows lock on ".dev" itself (the same class of bug DevRedirect exists to
        // avoid in the first place), which would then block unlink-program-files.py's own rmdir of
        // that same folder -- ToolLauncher.cs launches every tool with WorkingDirectory set to the
        // registry's own directory, which for a tool running under this respawned process would be
        // ".dev" itself.
        var startInfo = new ProcessStartInfo(devExePath)
        {
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new AppError($"failed to start dev redirect '{devExePath}'.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
