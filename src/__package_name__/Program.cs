using __package_name__.Base;

namespace __package_name__;

public sealed record CliArguments(bool Debug = false);

public static class Program
{
    public static int Main(string[] args) => Run(args);

    /// <summary>
    /// Testable entry point -- Main() just forwards here. settingsPath lets a test point at a
    /// fixture file instead of the real ./settings.json (see the Python template's
    /// test_main_runs_clean for the pattern this mirrors).
    /// </summary>
    public static int Run(string[]? argv = null, string? settingsPath = null)
    {
        // No explicit sink setup needed: Logger's default sink is already a live ConsoleLogSink
        // with safe bootstrap settings (see Diagnostics.cs), so anything logged before settings are
        // read -- a CLI-parsing error, a malformed settings.json -- is actually printed already;
        // ConfigureConsole below just narrows it to the real level/categories once settings load.
        var arguments = ParseArgs(argv ?? Array.Empty<string>());

        Settings settings;
        try
        {
            settings = settingsPath is null ? Settings.Load() : Settings.Load(path: settingsPath);
        }
        catch (AppError error)
        {
            Logger.Error($"__project_name__: error: {error.Message}");
            return 1;
        }

        Logger.ConfigureConsole(
            minLevel: settings.LogLevel,
            categories: settings.LogCategories,
            excludedCategories: settings.ExcludedCategories);

        var debug = settings.Debug || arguments.Debug;

        try
        {
            Logger.Info("__project_name__: started.");
            Logger.Info("__project_name__: completed.");
            return 0;
        }
        catch (AppError error)
        {
            if (debug)
            {
                throw;
            }

            Logger.Error($"__project_name__: error: {error.Message}");
            return 1;
        }
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
        foreach (var arg in argv)
        {
            if (arg == "--debug")
            {
                debug = true;
            }
            else if (arg is "-h" or "--help")
            {
                Logger.Print("usage: __project_name__ [--debug]");
                Logger.Print(string.Empty);
                Logger.Print("__description__");
                Logger.Print(string.Empty);
                Logger.Print("options:");
                Logger.Print("  --debug   override settings.json's debug flag");

                Environment.Exit(0);
            }
            else
            {
                Logger.Error($"__project_name__: error: unrecognized argument: {arg}");
                Environment.Exit(2);
            }
        }

        return new CliArguments(Debug: debug);
    }
}
