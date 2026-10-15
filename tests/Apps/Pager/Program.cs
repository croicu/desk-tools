using System.Runtime.ExceptionServices;
using System.Text.Json;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Service.Launcher;

namespace Croicu.Desk.Tools.Pager.App;

/// <summary>
/// End-to-end check of the ntfy pager (issue #76), through the real chain: makes sure Service is running and asks
/// it to launch the registered <c>pager</c> tool through the Launcher assembly (issue #74) - no <c>Desk.exe</c> -
/// so Service resolves its declared <c>NTFY_TOPIC</c> secret from KeePass (the master-password dialog - type it
/// yourself, the installed Service runs elevated) and starts it with the topic in its environment; this app then
/// speaks MCP to it over the connection's stdio.
///
/// By default it only <b>initializes</b> and <b>lists the tools</b> - proof that the tool launches and talks,
/// and nothing is sent. <c>--send</c> also calls <c>page</c> once and prints what the pager answered, which sends a
/// <b>real notification</b> to whatever topic the KeePass entry <c>ntfy</c>/<c>desk-pager</c> holds. With the dummy
/// database (<c>tests/data/Secrets.kdbx</c>) that topic is the all-zero GUID: a public, guessable one on ntfy.sh,
/// harmless but not private - use your real database for anything you care about.
///
/// Usage: <c>Pager.App [--send] [--timeout &lt;seconds&gt;]</c>. Exit code 0 = it worked (with
/// <c>--send</c>: the notification was accepted), 1 = it did not, 2 = bad usage.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Logger.SetLogger(ConsoleLog.Create(TelemetryLevel.Info));

        var send = false;
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
            else if (arg == "--timeout" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out timeoutSeconds) || timeoutSeconds <= 0)
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

        try
        {
            return Run(send, timeoutSeconds);
        }
        catch (AppError)
        {
            // The AppError already logged its own (detailed) message through Logger above.
            return Fail("see the log line above.");
        }
    }

    private static int Run(bool send, int timeoutSeconds)
    {
        Logger.Print(send
            ? "mode: --send (a real notification will be sent)"
            : "mode: check only (initialize + tools/list; nothing is sent -- add --send to page)");
        Logger.Print($"waiting up to {timeoutSeconds}s per step (the master-password dialog may be open: answer it).");

        var client = new Client(Settings.Load());
        Starter.EnsureRunning(client);

        // Blocks while Service resolves the secret (the dialog never times out), so it runs on its own thread to
        // keep the --timeout meaningful.
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var launching = Task.Run(() => client.LaunchTool("pager"));
        if (!WaitFor(launching, timeout))
        {
            return Fail($"Service did not launch the pager within {timeoutSeconds}s.");
        }

        using var tool = launching.Result;

        var initialized = Ask(tool, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Pager.App","version":"0.1.0"}}}""", timeout);
        if (initialized is null)
        {
            return Fail("no answer to initialize: the pager did not start.");
        }

        var server = initialized.RootElement.GetProperty("result").GetProperty("serverInfo");
        Logger.Print($"server: {server.GetProperty("name").GetString()} {server.GetProperty("version").GetString()}");

        tool.Input.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        var listed = Ask(tool, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", timeout);
        if (listed is null)
        {
            return Fail("no answer to tools/list.");
        }

        var names = new List<string>();
        foreach (var listedTool in listed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            names.Add(listedTool.GetProperty("name").GetString() ?? string.Empty);
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

        var paged = Ask(tool, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"page","arguments":{"message":"Pager check from desk-tools: if you can read this, the whole chain works.","title":"desk-tools pager","tags":["white_check_mark"]}}}""", timeout);
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

    /// <summary>Sends one request line and returns the next line the tool answers, or null if none arrives in time (or the tool ended).</summary>
    private static JsonDocument? Ask(ToolConnection tool, string request, TimeSpan timeout)
    {
        tool.Input.WriteLine(request);

        var reading = Task.Run(tool.Output.ReadLine);
        if (!WaitFor(reading, timeout) || reading.Result is null)
        {
            return null;
        }

        return JsonDocument.Parse(reading.Result);
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

    private static int Fail(string message)
    {
        Logger.Print($"FAILED: {message}");
        Logger.Print("Service's own log (logDir in settings) has the secrets/registry details.");
        return 1;
    }

    private static void PrintUsage()
    {
        Logger.Print("usage: Pager.App [--send] [--timeout <seconds>]");
        Logger.Print("  Launches the 'pager' tool through Service and speaks MCP to it. Default: initialize + tools/list only (nothing sent).");
        Logger.Print("  --send also pages once: a REAL notification to the topic in KeePass ntfy/desk-pager.");
    }
}
