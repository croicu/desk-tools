using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

public sealed record CliArguments(bool Debug = false, string? LogDir = null);

public static class Program
{
    public static int Main(string[] args) => Start(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (see the Python template's
    /// test_main_runs_clean for the pattern this mirrors). Not named Main itself: a Main with extra
    /// parameters beyond <c>string[] args</c> -- even optional ones -- isn't recognized as a valid
    /// CLR entry point at all (CS5001), so the settingsPath test hook has to live on a differently
    /// named method. Everything else -- console lifecycle, settings loading, Logger wiring,
    /// AppError-to-exit-code translation -- is <see cref="Context.Start"/>'s job (called from within
    /// here, hence the shared name -- always reached qualified as <c>Context.Start</c>, so no
    /// ambiguity with this method); this is just the app-specific pieces it can't own (CLI parsing,
    /// naming itself for Start's error-logging prefix) plus passing <see cref="Run"/> as the run
    /// body.
    /// </summary>
    internal static int Start(string[]? argv = null, string? settingsPath = null)
    {
        var arguments = ParseArgs(argv ?? []);

        return Context.Start(new ServiceConsole(), "desk-tools", settingsPath, arguments.Debug, arguments.LogDir, Run);
    }

    public static int Run()
    {
        Logger.Info("desk-tools: started.");

        // Left at its own install folder (the scheduled task registers no explicit "start in"
        // directory -- see installer/Package.wxs's CreateScheduledTask -- so Windows defaults it to
        // Service.exe's own containing folder), Service's own current directory would hold an
        // implicit Windows lock on that folder for its entire lifetime -- the same class of
        // "process cannot access the file" lock that scripts/link-program-files.py's own remarks
        // describe for a launched tool's cwd, just persistent instead of transient. Only
        // link-program-files.py/unlink-program-files.py's job of renaming that very folder actually
        // needs this today, but the fix belongs here rather than duplicated per-script: it's Service
        // itself, not any one tool, that's sitting in the folder for as long as it runs. Set after
        // Settings/Logger are already wired up (Context.Start's job, above this method), not before
        // -- Settings.Load's own working-directory settings.json tier is deliberately
        // cwd-relative (see src/Base/Settings.cs's DefaultSettingsPath), so moving cwd any earlier
        // would silently break that tier.
        Environment.CurrentDirectory = Environment.SystemDirectory;

        using var guard = SingletonGuard.TryAcquire(out var abandoned);
        if (guard is null)
        {
            Logger.Info("desk-tools: another instance is already running; exiting.");
            return 0;
        }

        if (abandoned)
        {
            Logger.Warning("desk-tools: the previous instance's single-instance guard was abandoned (it likely crashed); proceeding anyway.");
        }

        new Host(Settings.Current).Run();
        Logger.Info("desk-tools: completed.");

        return 0;
    }

    /// <summary>
    /// Hand-rolled instead of a CLI-parsing package (e.g. System.CommandLine) -- the Python
    /// template has zero runtime dependencies (stdlib argparse only), and --debug is the only flag
    /// this scaffold actually needs. Reach for a real package once the CLI surface grows past what
    /// this can comfortably express.
    /// </summary>
    internal static CliArguments ParseArgs(string[] argv)
    {
        var debug = false;
        string? logDir = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--debug")
            {
                debug = true;
            }
            else if (arg == "--log")
            {
                if (i + 1 >= argv.Length)
                {
                    Logger.Error("desk-tools: error: --log requires a directory argument");
                    Environment.Exit(2);
                }

                logDir = argv[++i];
            }
            else if (arg is "-h" or "--help")
            {
                Logger.Print("usage: desk-tools [--debug] [--log <dir>]");
                Logger.Print(string.Empty);
                Logger.Print("Authoring repo for Claude Code MCP servers and tools used by the ecosystem and distributed by desk-organizer. Produces artifacts; does not run them.");
                Logger.Print(string.Empty);
                Logger.Print("options:");
                Logger.Print("  --debug        override settings.json's debug flag");
                Logger.Print("  --log <dir>    override settings.json's logDir; write a timestamped log file into <dir>");

                Environment.Exit(0);
            }
            else
            {
                Logger.Error($"desk-tools: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        return new CliArguments(Debug: debug, LogDir: logDir);
    }
}
