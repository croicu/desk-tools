using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.McpSecrets.App;

/// <summary>
/// End-to-end check of secret delivery (issue #50, slice 4b), through the real chain: this app writes a
/// throwaway registry holding an <c>echo-secret</c> tool that declares one secret, runs
/// <c>desk mcp echo-secret --registry &lt;dir&gt;</c>, sends the tool a line and prints what it answers.
/// Service launches the tool like any other -- it neither knows nor cares that it is a plain stdio echo
/// (<c>scripts/echo-secret.py</c>, not an MCP server) -- resolves the declared secret from KeePass
/// (showing the master-password dialog, and the file picker the first time, if needed) and puts it in
/// the tool's environment; the tool answers <c>PROBE_SECRET=&lt;value&gt;</c>.
///
/// Prerequisites: a running (or auto-startable) Service built from this tree, and
/// <c>"allowCallerRegistry": true</c> in the <c>"settings"</c> object of the settings.local.json next to
/// it (the per-call registry is refused otherwise -- issue #69), <c>python</c> on the PATH, and KeePassXC
/// installed machine-wide. The default entry/key are the dummy database's (<c>tests/data/Secrets.kdbx</c>,
/// master password <c>password</c>): when no database is configured the file picker opens, pick that one.
/// Prints the value on purpose -- use the dummy database, not a real one, unless that is what you want.
///
/// Usage: <c>McpSecrets.App [--desk &lt;Desk.exe&gt;] [--entry &lt;entry&gt;] [--key &lt;key&gt;] [--expect &lt;value&gt;] [--timeout &lt;seconds&gt;]</c>.
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

        string? deskPath = null;
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
                case "--desk":
                    deskPath = Path.GetFullPath(value);
                    break;
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

        deskPath ??= FindDesk();
        if (deskPath is null || !File.Exists(deskPath))
        {
            Logger.Print($"error: Desk.exe not found{(deskPath is null ? string.Empty : $" at {deskPath}")}; pass --desk <path>.");
            return 2;
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
            return Run(deskPath, scriptSource, workDir, entry, key, expected, timeoutSeconds);
        }
        finally
        {
            DeleteQuietly(workDir);
        }
    }

    private static int Run(string deskPath, string scriptSource, string workDir, string entry, string key, string? expected, int timeoutSeconds)
    {
        WriteRegistry(workDir, scriptSource, entry, key);
        var deskLogDir = Path.Combine(workDir, "desk-log");

        Logger.Print($"desk:     {deskPath}");
        Logger.Print($"registry: {workDir}");
        Logger.Print($"secret:   {EnvName} <- entry '{entry}', key '{key}'");
        Logger.Print($"waiting up to {timeoutSeconds}s (the master-password dialog may be open: answer it).");

        var startInfo = new ProcessStartInfo(deskPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add(ToolName);
        startInfo.ArgumentList.Add("--registry");
        startInfo.ArgumentList.Add(workDir);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(deskLogDir);

        using var desk = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start Desk.exe.");
        try
        {
            desk.StandardInput.WriteLine("hello");
            desk.StandardInput.Flush();

            var reading = Task.Run(desk.StandardOutput.ReadLine);
            if (!reading.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                return Fail($"no reply within {timeoutSeconds}s.", deskLogDir);
            }

            var reply = reading.Result;
            if (reply is null)
            {
                return Fail($"Desk exited (code {ExitCodeOrUnknown(desk)}) before the tool replied.", deskLogDir);
            }

            return Check(reply, expected, deskLogDir);
        }
        finally
        {
            Stop(desk);
        }
    }

    private static int Check(string reply, string? expected, string deskLogDir)
    {
        if (!reply.StartsWith(ReplyPrefix, StringComparison.Ordinal))
        {
            return Fail($"unexpected reply: {reply}", deskLogDir);
        }

        var value = reply[ReplyPrefix.Length..];
        if (value == NotSet || value.Length == 0)
        {
            return Fail($"the tool started but {EnvName} was not delivered (cancelled dialog, wrong entry/key, KeePassXC missing?).", deskLogDir);
        }

        Logger.Print($"{EnvName} = {value}");
        if (expected is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            return Fail($"expected '{expected}'.", deskLogDir);
        }

        Logger.Print("OK: the secret reached the tool.");
        return 0;
    }

    private static int Fail(string message, string deskLogDir)
    {
        Logger.Print($"FAILED: {message}");
        PrintDeskLog(deskLogDir);
        Logger.Print("Service's own log (logDir in settings) has the secrets/registry details.");
        return 1;
    }

    private static void PrintDeskLog(string deskLogDir)
    {
        if (!Directory.Exists(deskLogDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(deskLogDir))
        {
            Logger.Print($"--- Desk log {Path.GetFileName(file)}:");
            // Desk may still be running and writing it, so open it shared.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                Logger.Print(line);
            }
        }
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

    /// <summary>Closing stdin is the normal way a client ends a proxy; kill only if it does not follow.</summary>
    private static void Stop(Process desk)
    {
        try
        {
            desk.StandardInput.Close();
            if (!desk.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                desk.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static string ExitCodeOrUnknown(Process desk)
    {
        return desk.WaitForExit(TimeSpan.FromSeconds(5)) ? desk.ExitCode.ToString() : "unknown";
    }

    /// <summary>The repo's own build output, found by walking up from this exe (out\Debug\... first), else the installed one.</summary>
    private static string? FindDesk()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "out", configuration, "net10.0", "win-x64", "Desk.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Desk Tools", "Desk.exe");
        return File.Exists(installed) ? installed : null;
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
        return arg is "--desk" or "--entry" or "--key" or "--expect" or "--timeout";
    }

    private static void PrintUsage()
    {
        Logger.Print("usage: McpSecrets.App [--desk <Desk.exe>] [--entry <entry>] [--key <key>] [--expect <value>] [--timeout <seconds>]");
        Logger.Print("  Launches a throwaway 'echo-secret' tool through Desk/Service with one declared secret and prints");
        Logger.Print("  what reaches it. Defaults: the dummy database's entry 'ntfy', key 'desk-pager'; Desk.exe from out\\.");
        Logger.Print("  Needs settings.allowCallerRegistry=true (settings.local.json next to Service), python, KeePassXC.");
    }
}
