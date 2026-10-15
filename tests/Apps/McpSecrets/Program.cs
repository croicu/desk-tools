using System.Runtime.ExceptionServices;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Service.Launcher;

namespace Croicu.Desk.Tools.McpSecrets.App;

/// <summary>
/// End-to-end check of secret delivery (issue #50, slice 4b), through the real chain: this app writes a
/// throwaway registry holding an <c>echo-secret</c> tool that declares one secret, makes sure Service is running and
/// asks it to launch that tool from the registry (the per-call registry, issue #69), sends the tool a line and prints
/// what it answers. It talks to Service itself through the Launcher assembly (issue #74): no <c>Desk.exe</c>.
/// Service launches the tool like any other -- it neither knows nor cares that it is a plain stdio echo
/// (<c>scripts/echo-secret.py</c>, not an MCP server) -- resolves the declared secret from KeePass
/// (showing the master-password dialog, and the file picker the first time, if needed) and puts it in
/// the tool's environment; the tool answers <c>PROBE_SECRET=&lt;value&gt;</c>.
///
/// Prerequisites: Service installed (the scheduled task starts it if it is not running) and built from this tree, and
/// <c>"allowCallerRegistry": true</c> in the <c>"settings"</c> object of the settings.local.json next to
/// it (the per-call registry is refused otherwise - issue #69), <c>python</c> on the PATH, and KeePassXC
/// installed machine-wide. The default entry/key are the dummy database's (<c>tests/data/Secrets.kdbx</c>,
/// master password <c>password</c>): when no database is configured the file picker opens, pick that one.
/// Prints the value on purpose - use the dummy database, not a real one, unless that is what you want.
///
/// Usage: <c>McpSecrets.App [--entry &lt;entry&gt;] [--key &lt;key&gt;] [--expect &lt;value&gt;] [--timeout &lt;seconds&gt;]</c>.
/// Exit code 0 = the secret arrived (and matched <c>--expect</c>, if given), 1 = it did not, 2 = bad usage.
/// </summary>
internal static class Program
{
    private const string ToolName = "echo-secret";
    private const string EnvName = "PROBE_SECRET";
    private const string ReplyPrefix = EnvName + "=";
    private const string NotSet = "<not set>";

    private static int Main(string[] args)
    {
        Logger.SetLogger(ConsoleLog.Create(TelemetryLevel.Info));

        var entry = "ntfy";
        var key = "desk-pager";
        string? expected = null;
        var timeoutSeconds = 300;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--help" || arg == "-h")
            {
                PrintUsage();
                return 0;
            }

            if (i + 1 >= args.Length || !IsOption(arg))
            {
                PrintUsage();
                return 2;
            }

            var value = args[++i];
            switch (arg)
            {
                case "--entry":
                    entry = value;
                    break;
                case "--key":
                    key = value;
                    break;
                case "--expect":
                    expected = value;
                    break;
                case "--timeout":
                    if (!int.TryParse(value, out timeoutSeconds) || timeoutSeconds <= 0)
                    {
                        PrintUsage();
                        return 2;
                    }

                    break;
            }
        }

        var scriptSource = Path.Combine(AppContext.BaseDirectory, "scripts", "echo-secret.py");
        if (!File.Exists(scriptSource))
        {
            Logger.Print($"error: {scriptSource} is missing (build the app).");
            return 2;
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"mcp-secrets-{Guid.NewGuid():N}");
        try
        {
            return Run(scriptSource, workDir, entry, key, expected, timeoutSeconds);
        }
        catch (AppError)
        {
            // The AppError already logged its own (detailed) message through Logger above.
            Logger.Print("FAILED: see the log line above. Service's own log (logDir in settings) has the secrets/registry details.");
            return 1;
        }
        finally
        {
            DeleteQuietly(workDir);
        }
    }

    private static int Run(string scriptSource, string workDir, string entry, string key, string? expected, int timeoutSeconds)
    {
        WriteRegistry(workDir, scriptSource, entry, key);

        Logger.Print($"registry: {workDir}");
        Logger.Print($"secret:   {EnvName} <- entry '{entry}', key '{key}'");
        Logger.Print($"waiting up to {timeoutSeconds}s (the master-password dialog may be open: answer it).");

        var client = new Client(Settings.Load());
        Starter.EnsureRunning(client);

        // Blocks while Service resolves the secret (the dialog never times out), so it runs on its own thread to
        // keep the --timeout meaningful.
        var launching = Task.Run(() => client.LaunchTool(ToolName, workDir));
        if (!WaitFor(launching, TimeSpan.FromSeconds(timeoutSeconds)))
        {
            return Fail($"Service did not launch the tool within {timeoutSeconds}s.");
        }

        using var tool = launching.Result;

        tool.Input.WriteLine("hello");

        var reading = Task.Run(tool.Output.ReadLine);
        if (!WaitFor(reading, TimeSpan.FromSeconds(timeoutSeconds)))
        {
            return Fail($"no reply within {timeoutSeconds}s.");
        }

        var reply = reading.Result;
        if (reply is null)
        {
            return Fail("the tool ended before it replied.");
        }

        return Check(reply, expected);
    }

    /// <summary>
    /// <see cref="Task.Wait(TimeSpan)"/>, but a failed task surfaces as the exception it failed with (an
    /// <see cref="AppError"/> from the Launcher, say) instead of an <see cref="AggregateException"/> wrapping it.
    /// Returns false on timeout.
    /// </summary>
    private static bool WaitFor(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static int Check(string reply, string? expected)
    {
        if (!reply.StartsWith(ReplyPrefix, StringComparison.Ordinal))
        {
            return Fail($"unexpected reply: {reply}");
        }

        var value = reply[ReplyPrefix.Length..];
        if (value == NotSet || value.Length == 0)
        {
            return Fail($"the tool started but {EnvName} was not delivered (cancelled dialog, wrong entry/key, KeePassXC missing?).");
        }

        Logger.Print($"{EnvName} = {value}");
        if (expected is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            return Fail($"expected '{expected}'.");
        }

        Logger.Print("OK: the secret reached the tool.");
        return 0;
    }

    private static int Fail(string message)
    {
        Logger.Print($"FAILED: {message}");
        Logger.Print("Service's own log (logDir in settings) has the secrets/registry details.");
        return 1;
    }

    /// <summary>Writes <c>&lt;workDir&gt;/mcp-registry/echo-secret.json</c> and the script next to it (args resolve against workDir).</summary>
    private static void WriteRegistry(string workDir, string scriptSource, string entry, string key)
    {
        Directory.CreateDirectory(Path.Combine(workDir, "mcp-registry"));
        Directory.CreateDirectory(Path.Combine(workDir, "scripts"));
        File.Copy(scriptSource, Path.Combine(workDir, "scripts", "echo-secret.py"));

        var fragment = new Dictionary<string, object>
        {
            ["name"] = ToolName,
            ["type"] = "stdio",
            ["command"] = "python",
            ["args"] = new[] { "scripts/echo-secret.py" },
            ["env"] = new Dictionary<string, string>(),
            ["secrets"] = new[] { new Dictionary<string, string> { ["env"] = EnvName, ["entry"] = entry, ["key"] = key } },
        };
        File.WriteAllText(Path.Combine(workDir, "mcp-registry", ToolName + ".json"), JsonSerializer.Serialize(fragment));
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }
    }

    private static bool IsOption(string arg)
    {
        return arg is "--entry" or "--key" or "--expect" or "--timeout";
    }

    private static void PrintUsage()
    {
        Logger.Print("usage: McpSecrets.App [--entry <entry>] [--key <key>] [--expect <value>] [--timeout <seconds>]");
        Logger.Print("  Launches a throwaway 'echo-secret' tool through Service with one declared secret and prints");
        Logger.Print("  what reaches it. Defaults: the dummy database's entry 'ntfy', key 'desk-pager'.");
        Logger.Print("  Needs settings.allowCallerRegistry=true (settings.local.json next to Service), python, KeePassXC.");
    }
}
