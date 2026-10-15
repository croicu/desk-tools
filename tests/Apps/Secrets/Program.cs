using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;
using Croicu.Desk.Tools.Secrets;

namespace Croicu.Desk.Tools.Secrets.App;

/// <summary>
/// Manual driver for <c>Secrets.dll</c> (issue #50): shows the real master-password dialog, opens a
/// KeePass database through <c>keepassxc-cli</c>, and prints the secret it finds. Exists so the
/// whole path -- dialog, retries, CLI, parsing -- can be exercised and stepped through by hand (set
/// this as the startup project in Visual Studio). Not shipped, and it prints the value on purpose:
/// run it against the dummy database, not a real one, unless that is what you want.
///
/// Usage: <c>Secrets.App [--picker] [--user-settings &lt;file&gt;] [databasePath] [entry] [key]</c>.
/// Without <c>--picker</c> the database is <c>databasePath</c>, defaulting to the dummy database next
/// to the exe (master password <c>password</c>). With <c>--picker</c> the database comes from the real
/// resolution logic instead: the saved <c>secrets.databasePath</c> setting if there is one, otherwise
/// the file dialog, whose answer is saved. <c>--user-settings</c> redirects that saved setting to a
/// scratch file so trying the picker doesn't touch your real per-user settings file (the default).
/// Entry defaults to <c>ntfy</c>, key to <c>desk-pager</c>. Exit code 0 = secret printed, 1 = failed
/// (cancelled, wrong password three times, missing entry/key, ...), 2 = bad usage or not Windows.
/// </summary>
internal static class Program
{
    private const string DefaultEntry = "ntfy";
    private const string DefaultKey = "desk-pager";

    private static int Main(string[] args)
    {
        // Log lines (asking for the password, attempts, outcomes) show on the console too; the secret
        // itself is printed separately below.
        Logger.SetLogger(ConsoleLog.Create(TelemetryLevel.Verbose));

        var usePicker = false;
        string? userSettingsPath = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--help" || args[i] == "-h")
            {
                PrintUsage();
                return 0;
            }

            if (args[i] == "--picker")
            {
                usePicker = true;
            }
            else if (args[i] == "--user-settings")
            {
                if (i + 1 >= args.Length)
                {
                    Logger.Print("error: --user-settings requires a file argument.");
                    return 2;
                }

                userSettingsPath = Path.GetFullPath(args[++i]);
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        if (positional.Count > 3 || (usePicker && positional.Count > 2))
        {
            PrintUsage();
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Logger.Print("error: the dialogs are only implemented on Windows (see issue #51).");
            return 2;
        }

        // With --picker the database comes from the settings/dialog, so the positionals are just
        // [entry] [key]; without it they are [databasePath] [entry] [key].
        var firstEntryIndex = usePicker ? 0 : 1;
        var entry = positional.Count > firstEntryIndex ? positional[firstEntryIndex] : DefaultEntry;
        var key = positional.Count > firstEntryIndex + 1 ? positional[firstEntryIndex + 1] : DefaultKey;

        if (usePicker)
        {
            return RunWithPicker(userSettingsPath, entry, key);
        }

        var databasePath = positional.Count > 0 ? Path.GetFullPath(positional[0]) : Path.Combine(AppContext.BaseDirectory, "data", "Secrets.kdbx");
        if (!File.Exists(databasePath))
        {
            Logger.Print($"error: database not found: {databasePath}");
            return 2;
        }

        return Run(databasePath, entry, key);
    }

    private static void PrintUsage()
    {
        Logger.Print("usage: Secrets.App [--picker] [--user-settings <file>] [databasePath] [entry] [key]");
        Logger.Print("  default: the dummy database next to the exe, entry 'ntfy', key 'desk-pager'.");
        Logger.Print("  --picker: take the database from the saved 'secrets.databasePath' setting, or ask with the file");
        Logger.Print("            dialog (and save the answer); then [entry] [key] are the only positionals.");
        Logger.Print("  --user-settings <file>: use this scratch file instead of the real per-user settings file.");
    }

    [SupportedOSPlatform("windows")]
    private static int RunWithPicker(string? userSettingsPath, string entry, string key)
    {
        try
        {
            var location = new DatabaseLocation(new DatabasePathStore(userPath: userSettingsPath), new FilePicker());
            var databasePath = location.Resolve();
            return Run(databasePath, entry, key);
        }
        catch (AppError)
        {
            // The AppError already logged its own (detailed) message through Logger above.
            Logger.Print("failed -- see the log line above.");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int Run(string databasePath, string entry, string key)
    {
        Logger.Print($"database: {databasePath}");
        Logger.Print($"entry:    {entry}");
        Logger.Print($"key:      {key}");

        try
        {
            var cliPath = CliLocation.Resolve();
            using var passwords = new PromptedPassword(new PasswordPrompt());
            var vault = new Vault(databasePath, new KeePassCli(new ProcessRunner(), cliPath), passwords);

            var values = vault.Read(entry, [key]);
            var value = values[key];
            try
            {
                Logger.Print($"{key} = {Encoding.UTF8.GetString(value)}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
            }

            return 0;
        }
        catch (AppError)
        {
            // The AppError already logged its own (detailed) message through Logger above.
            Logger.Print("failed -- see the log line above.");
            return 1;
        }
    }
}
