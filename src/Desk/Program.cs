using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Service.Launcher;

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
    ForgetPassword,
    Mcp,
}

/// <summary>
/// Command-line wrapper over the Launcher assembly (<c>Croicu.Desk.Tools.Service.Launcher</c>, issue #74), which does
/// the real work of talking to <c>src/Service</c>'s <c>Host</c>: this class only parses arguments, sets up
/// logging and the console, and turns the Launcher's answers into output and exit codes. <c>ping</c> and <c>mcp</c>
/// make sure Service is running first (<see cref="Starter.EnsureRunning"/>); <c>shutdown</c> never starts it
/// (shutting down something that isn't running isn't an error worth auto-starting for). Failures surface via the
/// same <see cref="AppError"/>-to-exit-code-1 handling every other app in this repo already uses (see
/// <see cref="Context.Start"/>). <see cref="McpToolName"/> is only ever set alongside
/// <see cref="DeskCommand.Mcp"/> -- <see cref="Program.ParseArgs"/> requires it as that
/// subcommand's own positional argument. <see cref="RegistryDir"/> (<c>mcp</c> only, <c>--registry
/// &lt;dir&gt;</c>) is a caller-chosen MCP tool registry for Service to search first -- Service honors it
/// only when its own <c>settings.allowCallerRegistry</c> is on (issue #69).
/// </summary>
public sealed record CliArguments(DeskCommand Command, string? McpToolName = null, string? LogDir = null, string? RegistryDir = null);

public static class Program
{
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
    /// stdout must carry only that traffic, the same reasoning <c>tests/MCP/Hello/Program.cs</c>'s own
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
                Starter.EnsureRunning(client);
                Logger.Print(client.Ping());
                break;
            case DeskCommand.Shutdown:
                return Shutdown(client);
            case DeskCommand.ForgetPassword:
                // Only Service can: the remembered password is in a file nothing unelevated may touch.
                Starter.EnsureRunning(client);
                client.ForgetPassword();
                Logger.Print("The master password was forgotten; you will be asked for it again.");
                break;
            case DeskCommand.Mcp:
                // Service needs a fully qualified path (it has a different working directory).
                var registryDir = arguments.RegistryDir is null ? null : Path.GetFullPath(arguments.RegistryDir);
                Starter.EnsureRunning(client);
                using (var tool = client.LaunchTool(arguments.McpToolName!, registryDir))
                {
                    // Non-zero when the tool went away before the client did -- see McpProxy.Run.
                    return McpProxy.Run(tool);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(arguments), arguments.Command, "Unhandled DeskCommand.");
        }

        return 0;
    }

    /// <summary>
    /// <c>desk shutdown</c>. A Service that is not running is not a failure -- there is nothing to shut down, which
    /// is exactly the state asked for -- so it is reported plainly and exits 0, instead of surfacing as an error
    /// (and, with <c>debug</c> on, a stack trace). Any other failure is still an error.
    /// </summary>
    internal static int Shutdown(Client client)
    {
        try
        {
            client.Shutdown();
        }
        catch (NotRunningError)
        {
            Logger.Print("The service is not running; nothing to shut down.");
            return 0;
        }

        Logger.Print("Shutdown requested.");
        return 0;
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
        string? registryDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "ping" || arg == "shutdown" || arg == "forget-password" || arg == "mcp")
            {
                if (command is not null)
                {
                    Logger.Error("desk: error: 'ping', 'shutdown', 'forget-password', and 'mcp' are mutually exclusive");
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
                    command = arg switch
                    {
                        "ping" => DeskCommand.Ping,
                        "forget-password" => DeskCommand.ForgetPassword,
                        _ => DeskCommand.Shutdown,
                    };
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
            else if (arg == "--registry")
            {
                if (i + 1 >= argv.Length)
                {
                    Logger.Error("desk: error: --registry requires a directory argument");
                    Environment.Exit(2);
                }

                registryDir = argv[++i];
            }
            else
            {
                Logger.Error($"desk: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        if (command is null)
        {
            Logger.Error("desk: error: usage: desk <ping|shutdown|forget-password|mcp <name>> [--log <dir>] [--registry <dir>]");
            Environment.Exit(2);
        }

        if (registryDir is not null && command != DeskCommand.Mcp)
        {
            Logger.Error("desk: error: --registry only applies to 'mcp'");
            Environment.Exit(2);
        }

        return new CliArguments(Command: command!.Value, McpToolName: mcpToolName, LogDir: logDir, RegistryDir: registryDir);
    }
}
