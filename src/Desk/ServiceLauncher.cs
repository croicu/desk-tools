using System.Diagnostics;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Starts a Service instance when Desk's own ping can't reach one, then waits until it's actually
/// reachable. Goes through the installed scheduled task (`schtasks /run /tn "Desk Tools Service"`,
/// see installer/Package.wxs, issue #19) rather than directly launching Service.dll as a plain child
/// process: Service is meant to run at the highest integrity level the account allows (the scheduled
/// task's own `/rl highest`) -- a plain `Process.Start("dotnet", ...)` from Desk would instead run it
/// at Desk's own (typically lower, unelevated) integrity, defeating that entirely and potentially
/// leaving two instances around (a never-triggered elevated one the task would have started, plus an
/// unprivileged one squatting the port instead). `schtasks /run` itself needs no elevation from the
/// caller -- Task Scheduler runs the task under its own configured principal regardless of the
/// triggering process's own privilege, same as the existing `.vscode/tasks.json` "start service" task
/// already relies on.
///
/// A consequence worth being explicit about: auto-start only works once Service has actually been
/// installed via the MSI (the scheduled task must already be registered) -- it can't stand up a
/// plain local dev-loop `dotnet Service.dll` instance. That's intentional, not an oversight: silently
/// falling back to an unprivileged spawn would be the exact mistake this class exists to avoid.
/// </summary>
internal static class ServiceLauncher
{
    private const string ScheduledTaskName = "Desk Tools Service";
    // Generous on purpose: a cold first start (freshly built binaries reached through the .dev
    // junction, after a long idle spell) was observed taking ~29s from the Service stub starting to
    // the dev Service actually listening. A 10s wait made several `desk mcp <name>` processes started
    // together at session start give up and exit, which Claude Code reports as a bare
    // CONNECTION_CLOSED. A warm start still returns on the first successful poll, so the extra
    // headroom costs nothing normally.
    private static readonly TimeSpan DefaultStartupWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Runs the scheduled task and blocks until <paramref name="isReachable"/> reports success or
    /// <paramref name="timeout"/> elapses. Throws <see cref="AppError"/> if `schtasks /run` itself
    /// fails (e.g. the task isn't registered -- Service was never installed) or if the service never
    /// becomes reachable within the timeout -- both are genuine failures the caller should surface
    /// (via the same AppError-to-exit-code-1 handling every other failure here already gets), not
    /// silently swallow.
    /// </summary>
    public static void StartAndWaitUntilReachable(Func<bool> isReachable, TimeSpan? timeout = null)
    {
        Logger.Info($"desk: starting the service via the scheduled task ('{ScheduledTaskName}').");

        // Output is captured, never inherited: for `desk mcp` Desk's own stdout is the MCP protocol stream, and
        // schtasks' "SUCCESS: Attempted to run the scheduled task..." line would otherwise be the first thing
        // the client reads from it.
        var startInfo = new ProcessStartInfo("schtasks")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/run");
        startInfo.ArgumentList.Add("/tn");
        startInfo.ArgumentList.Add(ScheduledTaskName);

        using var schtasks = Process.Start(startInfo) ?? throw new AppError("could not auto-start the service: failed to launch schtasks.exe.");

        // Both streams are a line or two, so reading them one after the other cannot fill a pipe.
        var output = schtasks.StandardOutput.ReadToEnd().Trim();
        var errors = schtasks.StandardError.ReadToEnd().Trim();
        schtasks.WaitForExit();
        if (schtasks.ExitCode != 0)
        {
            throw new AppError($"could not auto-start the service: 'schtasks /run /tn \"{ScheduledTaskName}\"' exited with code {schtasks.ExitCode} ({errors}) -- is Service installed? (see installer/Package.wxs, issue #19)");
        }

        Logger.Diagnostic($"desk: schtasks: {output}");

        var deadline = DateTime.UtcNow + (timeout ?? DefaultStartupWait);
        while (DateTime.UtcNow < deadline)
        {
            if (isReachable())
            {
                Logger.Info("desk: the service is now reachable.");
                return;
            }

            Thread.Sleep(PollInterval);
        }

        throw new AppError("the service was started but never became reachable within the startup wait.");
    }
}
