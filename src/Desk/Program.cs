using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// Console client for <c>src/Service</c>: connects to <c>Host</c>'s loopback TCP listener, sends one
/// echo request, waits for the reply, prints it, then exits -- see <see cref="Client"/> for the
/// actual wire exchange and docs/PROTOCOL.md for the format. Fails fast (no retry, no auto-starting
/// the service) with a clear error when the service isn't reachable, surfaced via the same
/// <see cref="AppError"/>-to-exit-code-1 handling every other app in this repo already uses (see
/// <see cref="Context.Start"/>).
/// </summary>
public sealed record CliArguments(string? LogDir = null);

public static class Program
{
    private const string EchoMessage = "ping";

    public static int Main(string[] args) => Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (same convention as Service's/Hello's own
    /// Program.cs). No CLI-driven debug override, hence the literal false -- settings.debug alone
    /// still drives it if set.
    /// </summary>
    internal static int Start(string[]? argv = null, string? settingsPath = null)
    {
        var arguments = ParseArgs(argv ?? []);

        return Context.Start(new VoidConsole(), "desk", settingsPath, debugOverride: false, arguments.LogDir, Run);
    }

    public static int Run()
    {
        var reply = new Client(Settings.Current).SendEcho(EchoMessage);
        Logger.Print(reply);

        return 0;
    }

    /// <summary>
    /// Hand-rolled, mirrors Service's/Hello's own ParseArgs -- <c>--log &lt;dir&gt;</c> is the only
    /// flag Desk needs today.
    /// </summary>
    internal static CliArguments ParseArgs(string[] argv)
    {
        string? logDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--log")
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

        return new CliArguments(LogDir: logDir);
    }
}
