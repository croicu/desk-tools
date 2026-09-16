using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Which action Desk was invoked to perform -- a subcommand (<c>desk ping</c>/<c>desk shutdown</c>/
/// <c>desk mcp &lt;name&gt;</c>), not a flag: the three are mutually exclusive verbs (what Desk should
/// do), not independent options that could combine, so they don't belong alongside a toggle like
/// <c>--log</c>.
/// </summary>
public enum DeskCommand
{
    Ping,
    Shutdown,
    Mcp,
}

/// <summary>
/// Console client for <c>src/Service</c>: connects to <c>Host</c>'s loopback TCP listener, sends one
/// request line, waits for the reply, prints it, then exits -- see <see cref="Client"/> for the
/// actual wire exchange and docs/PROTOCOL.md for the format. <c>ping</c> auto-starts the service
/// (see <see cref="ServiceLauncher"/>) if it isn't reachable, then retries once; <c>shutdown</c>
/// stays fail-fast, no retry, no auto-start (shutting down something that isn't running isn't an
/// error worth auto-starting for) -- both failure modes surface via the same
/// <see cref="AppError"/>-to-exit-code-1 handling every other app in this repo already uses (see
/// <see cref="Context.Start"/>). <see cref="McpToolName"/> is only ever set alongside
/// <see cref="DeskCommand.Mcp"/> -- <see cref="Program.ParseArgs"/> requires it as that
/// subcommand's own positional argument.
/// </summary>
public sealed record CliArguments(DeskCommand Command, string? McpToolName = null, string? LogDir = null);

public static class Program
{
    // Must match Host.PingMethod/Host.ShutdownMethod/Host.McpMethod -- see those constants' own
    // remarks on why these are hand-kept-in-sync literals rather than a shared reference (Desk
    // deliberately has no ProjectReference on Service.csproj).
    private const string PingMethod = "ping";
    private const string ShutdownMethod = "shutdown";

    // See DevRedirect's own remarks -- must run before anything else Main does. No-op (returns
    // null) unless a ".dev" junction actually exists alongside this exe's own install folder.
    public static int Main(string[] args) => DevRedirect.TryHandoff("Desk.exe", args) ?? Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (same convention as Service's/Hello's own
    /// Program.cs). No CLI-driven debug override, hence the literal false -- settings.debug alone
    /// still drives it if set. Forwards arguments into Run via a trivial forwarding lambda (same
    /// pattern Hello's own Start uses for its input parameter) since Context.Start's run delegate is
    /// a plain Func&lt;int&gt;.
    ///
    /// For <see cref="DeskCommand.Mcp"/>, installs a silent <see cref="DiagnosticsLog"/> sink
    /// *before* <see cref="Context.Start"/> can install a real, printing one -- once
    /// <see cref="McpProxy.Run"/> starts relaying the launched tool's own stdout, this process's
    /// stdout must carry only that traffic, the same reasoning <c>src/Hello/Program.cs</c>'s own
    /// <c>Start</c> already documents for itself (no <c>ProjectReference</c> to cross-link a real
    /// <c>&lt;see cref&gt;</c> to it).
    /// </summary>
    internal static int Start(string[]? argv = null, string? settingsPath = null)
    {
        var arguments = ParseArgs(argv ?? []);

        if (arguments.Command == DeskCommand.Mcp)
        {
            Logger.SetLogger(new DiagnosticsLog());
        }

        return Context.Start(new VoidConsole(), "desk", settingsPath, debugOverride: false, arguments.LogDir, () => Run(arguments));
    }

    public static int Run(CliArguments arguments)
    {
        var client = new Client(Settings.Current);

        switch (arguments.Command)
        {
            case DeskCommand.Ping:
                Logger.Print(PingWithAutoStart(client));
                break;
            case DeskCommand.Shutdown:
                client.Send(ShutdownMethod);
                Logger.Print("Shutdown requested.");
                break;
            case DeskCommand.Mcp:
                var (stdin, stdout) = LaunchMcpToolWithAutoStart(client, arguments.McpToolName!);
                McpProxy.Run(stdin, stdout);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arguments), arguments.Command, "Unhandled DeskCommand.");
        }

        return 0;
    }

    /// <summary>
    /// Tries a normal ping first; only on failure does it fall back to starting the service and
    /// retrying, rather than always paying the auto-start machinery's cost. The retry after a
    /// successful auto-start is a plain, un-retried <see cref="Client.Send"/> call -- if the
    /// service somehow stops being reachable in the brief window between
    /// <see cref="ServiceLauncher.StartAndWaitUntilReachable"/> confirming it and this final call,
    /// that's a genuine failure worth surfacing as-is, not silently retried again.
    /// </summary>
    private static string PingWithAutoStart(Client client)
    {
        try
        {
            return client.Send(PingMethod);
        }
        catch (AppError)
        {
            Logger.Info("desk: service not reachable; attempting to start it.");
            ServiceLauncher.StartAndWaitUntilReachable(() => TryPing(client));
            return client.Send(PingMethod);
        }
    }

    private static bool TryPing(Client client)
    {
        try
        {
            client.Send(PingMethod);
            return true;
        }
        catch (AppError)
        {
            return false;
        }
    }

    /// <summary>
    /// Same auto-start-on-first-failure shape as <see cref="PingWithAutoStart"/> -- an MCP client
    /// invoking <c>desk mcp &lt;name&gt;</c> wants this to just work even if Service hasn't been
    /// started yet. Also retries on a non-reachability <see cref="AppError"/> (e.g. an unregistered
    /// tool name) the same way <see cref="PingWithAutoStart"/> would for an equivalent case -- a
    /// known, minor imprecision inherited from that existing pattern rather than a new one.
    /// </summary>
    private static (long Stdin, long Stdout) LaunchMcpToolWithAutoStart(Client client, string name)
    {
        try
        {
            return client.LaunchMcpTool(name);
        }
        catch (AppError)
        {
            Logger.Info("desk: service not reachable; attempting to start it.");
            ServiceLauncher.StartAndWaitUntilReachable(() => TryPing(client));
            return client.LaunchMcpTool(name);
        }
    }

    /// <summary>
    /// Hand-rolled, mirrors Service's/Hello's own ParseArgs. <c>ping</c>/<c>shutdown</c>/<c>mcp</c>
    /// are bare subcommand words, not <c>--</c>-prefixed flags (see <see cref="DeskCommand"/>'s own
    /// remarks); exactly one is required. <c>--log &lt;dir&gt;</c> remains a flag, valid alongside any.
    /// </summary>
    internal static CliArguments ParseArgs(string[] argv)
    {
        DeskCommand? command = null;
        string? mcpToolName = null;
        string? logDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "ping" || arg == "shutdown" || arg == "mcp")
            {
                if (command is not null)
                {
                    Logger.Error("desk: error: 'ping', 'shutdown', and 'mcp' are mutually exclusive");
                    Environment.Exit(2);
                }

                if (arg == "mcp")
                {
                    if (i + 1 >= argv.Length)
                    {
                        Logger.Error("desk: error: 'mcp' requires a tool name argument");
                        Environment.Exit(2);
                    }

                    command = DeskCommand.Mcp;
                    mcpToolName = argv[++i];
                }
                else
                {
                    command = arg == "ping" ? DeskCommand.Ping : DeskCommand.Shutdown;
                }
            }
            else if (arg == "--log")
            {
                if (i + 1 >= argv.Length)
                {
                    Logger.Error("desk: error: --log requires a directory argument");
                    Environment.Exit(2);
                }

                logDir = argv[++i];
            }
            else
            {
                Logger.Error($"desk: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        if (command is null)
        {
            Logger.Error("desk: error: usage: desk <ping|shutdown|mcp <name>> [--log <dir>]");
            Environment.Exit(2);
        }

        return new CliArguments(Command: command!.Value, McpToolName: mcpToolName, LogDir: logDir);
    }
}
