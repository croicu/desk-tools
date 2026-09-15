using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Which action Desk was invoked to perform -- a subcommand (<c>desk ping</c>/<c>desk shutdown</c>),
/// not a flag: the two are mutually exclusive verbs (what Desk should do), not independent options
/// that could combine, so they don't belong alongside a toggle like <c>--log</c>.
/// </summary>
public enum DeskCommand
{
    Ping,
    Shutdown,
}

/// <summary>
/// Console client for <c>src/Service</c>: connects to <c>Host</c>'s loopback TCP listener, sends one
/// request line, waits for the reply, prints it, then exits -- see <see cref="Client"/> for the
/// actual wire exchange and docs/PROTOCOL.md for the format. Fails fast (no retry, no auto-starting
/// the service) with a clear error when the service isn't reachable, surfaced via the same
/// <see cref="AppError"/>-to-exit-code-1 handling every other app in this repo already uses (see
/// <see cref="Context.Start"/>).
/// </summary>
public sealed record CliArguments(DeskCommand Command, string? LogDir = null);

public static class Program
{
    private const string PingMessage = "ping";

    // Must match Host.ShutdownCommand -- see that constant's own remarks on why this is a
    // hand-kept-in-sync literal rather than a shared reference (Desk deliberately has no
    // ProjectReference on Service.csproj).
    private const string ShutdownCommand = "shutdown";

    public static int Main(string[] args) => Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (same convention as Service's/Hello's own
    /// Program.cs). No CLI-driven debug override, hence the literal false -- settings.debug alone
    /// still drives it if set. Forwards arguments.Command into Run via a trivial forwarding lambda
    /// (same pattern Hello's own Start uses for its input parameter) since Context.Start's run
    /// delegate is a plain Func&lt;int&gt;.
    /// </summary>
    internal static int Start(string[]? argv = null, string? settingsPath = null)
    {
        var arguments = ParseArgs(argv ?? []);

        return Context.Start(new VoidConsole(), "desk", settingsPath, debugOverride: false, arguments.LogDir, () => Run(arguments.Command));
    }

    public static int Run(DeskCommand command)
    {
        var client = new Client(Settings.Current);

        switch (command)
        {
            case DeskCommand.Ping:
                Logger.Print(client.SendEcho(PingMessage));
                break;
            case DeskCommand.Shutdown:
                client.SendEcho(ShutdownCommand);
                Logger.Print("Shutdown requested.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unhandled DeskCommand.");
        }

        return 0;
    }

    /// <summary>
    /// Hand-rolled, mirrors Service's/Hello's own ParseArgs. <c>ping</c>/<c>shutdown</c> are bare
    /// subcommand words, not <c>--</c>-prefixed flags (see <see cref="DeskCommand"/>'s own remarks);
    /// exactly one is required. <c>--log &lt;dir&gt;</c> remains a flag, valid alongside either.
    /// </summary>
    internal static CliArguments ParseArgs(string[] argv)
    {
        DeskCommand? command = null;
        string? logDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "ping" || arg == "shutdown")
            {
                if (command is not null)
                {
                    Logger.Error("desk: error: 'ping' and 'shutdown' are mutually exclusive");
                    Environment.Exit(2);
                }

                command = arg == "ping" ? DeskCommand.Ping : DeskCommand.Shutdown;
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
            Logger.Error("desk: error: usage: desk <ping|shutdown> [--log <dir>]");
            Environment.Exit(2);
        }

        return new CliArguments(Command: command!.Value, LogDir: logDir);
    }
}
