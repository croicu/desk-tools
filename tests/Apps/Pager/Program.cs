using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Pager.App;

/// <summary>
/// End-to-end check of the ntfy pager (issue #76), through the real chain: runs <c>desk mcp pager</c>, so
/// Service launches the registered <c>pager</c> tool, resolves its declared <c>NTFY_TOPIC</c> secret from KeePass
/// (the master-password dialog -- type it yourself, the installed Service runs elevated) and starts it with the
/// topic in its environment; this app then speaks MCP to it over the proxied stdio.
///
/// By default it only <b>initializes</b> and <b>lists the tools</b> -- proof that the tool launches and talks,
/// and nothing is sent. <c>--send</c> also calls <c>page</c> once and prints what the pager answered, which sends a
/// <b>real notification</b> to whatever topic the KeePass entry <c>ntfy</c>/<c>desk-pager</c> holds. With the dummy
/// database (<c>tests/data/Secrets.kdbx</c>) that topic is the all-zero GUID: a public, guessable one on ntfy.sh,
/// harmless but not private -- use your real database for anything you care about.
///
/// Usage: <c>Pager.App [--send] [--desk &lt;Desk.exe&gt;] [--timeout &lt;seconds&gt;]</c>. Exit code 0 = it worked (with
/// <c>--send</c>: the notification was accepted), 1 = it did not, 2 = bad usage.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Logger.SetLogger(ConsoleLog.Create(TelemetryLevel.Info));

        var send = false;
        string? deskPath = null;
        var timeoutSeconds = 300;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--help" || arg == "-h")
            {
                PrintUsage();
                return 0;
            }

            if (arg == "--send")
            {
                send = true;
            }
            else if ((arg == "--desk" || arg == "--timeout") && i + 1 < args.Length)
            {
                var value = args[++i];
                if (arg == "--desk")
                {
                    deskPath = Path.GetFullPath(value);
                }
                else if (!int.TryParse(value, out timeoutSeconds) || timeoutSeconds <= 0)
                {
                    PrintUsage();
                    return 2;
                }
            }
            else
            {
                PrintUsage();
                return 2;
            }
        }

        deskPath ??= FindDesk();
        if (deskPath is null || !File.Exists(deskPath))
        {
            Logger.Print($"error: Desk.exe not found{(deskPath is null ? string.Empty : $" at {deskPath}")}; pass --desk <path>.");
            return 2;
        }

        return Run(deskPath, send, timeoutSeconds);
    }

    private static int Run(string deskPath, bool send, int timeoutSeconds)
    {
        Logger.Print($"desk: {deskPath}");
        Logger.Print(send
            ? "mode: --send (a real notification will be sent)"
            : "mode: check only (initialize + tools/list; nothing is sent -- add --send to page)");
        Logger.Print($"waiting up to {timeoutSeconds}s per step (the master-password dialog may be open: answer it).");

        var startInfo = new ProcessStartInfo(deskPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("pager");

        using var desk = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start Desk.exe.");
        try
        {
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);

            var initialized = Ask(desk, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Pager.App","version":"0.1.0"}}}""", timeout);
            if (initialized is null)
            {
                return Fail("no answer to initialize: the pager did not start (is Service installed and was the dialog answered?).");
            }

            var server = initialized.RootElement.GetProperty("result").GetProperty("serverInfo");
            Logger.Print($"server: {server.GetProperty("name").GetString()} {server.GetProperty("version").GetString()}");

            desk.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            desk.StandardInput.Flush();

            var listed = Ask(desk, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", timeout);
            if (listed is null)
            {
                return Fail("no answer to tools/list.");
            }

            var names = new List<string>();
            foreach (var tool in listed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
            {
                names.Add(tool.GetProperty("name").GetString() ?? string.Empty);
            }

            Logger.Print($"tools: {string.Join(", ", names)}");
            if (!names.Contains("page"))
            {
                return Fail("the 'page' tool is missing.");
            }

            if (!send)
            {
                Logger.Print("OK: the pager launched through Service and answers MCP. Nothing was sent.");
                return 0;
            }

            var paged = Ask(desk, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"page","arguments":{"message":"Pager check from desk-tools: if you can read this, the whole chain works.","title":"desk-tools pager","tags":["white_check_mark"]}}}""", timeout);
            if (paged is null)
            {
                return Fail("no answer to the page call.");
            }

            var result = paged.RootElement.GetProperty("result");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            var isError = result.GetProperty("isError").GetBoolean();
            Logger.Print($"page: {text}");
            if (isError)
            {
                return Fail("the pager reported an error (not configured? server refused?).");
            }

            Logger.Print("OK: the notification was accepted by the ntfy server.");
            return 0;
        }
        finally
        {
            Stop(desk);
        }
    }

    /// <summary>Sends one request line and returns the next line Desk relays, or null if none arrives in time (or Desk ended).</summary>
    private static JsonDocument? Ask(Process desk, string request, TimeSpan timeout)
    {
        desk.StandardInput.WriteLine(request);
        desk.StandardInput.Flush();

        var reading = Task.Run(desk.StandardOutput.ReadLine);
        if (!reading.Wait(timeout) || reading.Result is null)
        {
            return null;
        }

        return JsonDocument.Parse(reading.Result);
    }

    private static int Fail(string message)
    {
        Logger.Print($"FAILED: {message}");
        Logger.Print("Service's own log (logDir in settings) has the secrets/registry details.");
        return 1;
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

    private static void PrintUsage()
    {
        Logger.Print("usage: Pager.App [--send] [--desk <Desk.exe>] [--timeout <seconds>]");
        Logger.Print("  Runs 'desk mcp pager' and speaks MCP to it. Default: initialize + tools/list only (nothing sent).");
        Logger.Print("  --send also pages once: a REAL notification to the topic in KeePass ntfy/desk-pager.");
    }
}
