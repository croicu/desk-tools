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
/// Usage: <c>Secrets.App [databasePath] [entry] [key]</c> -- all optional, defaulting to the dummy
/// database next to the exe (master password <c>password</c>), entry <c>ntfy</c>, key
/// <c>desk-pager</c>. Exit code 0 = secret printed, 1 = failed (cancelled, wrong password three
/// times, missing entry/key, ...), 2 = bad usage or not Windows.
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

        if (args.Length > 3 || args.Contains("--help") || args.Contains("-h"))
        {
            Logger.Print("usage: Secrets.App [databasePath] [entry] [key]");
            Logger.Print($"  defaults: the dummy database next to the exe, entry '{DefaultEntry}', key '{DefaultKey}'.");
            return args.Length > 3 ? 2 : 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Logger.Print("error: the master password dialog is only implemented on Windows (see issue #51).");
            return 2;
        }

        var databasePath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "data", "Secrets.kdbx");
        var entry = args.Length > 1 ? args[1] : DefaultEntry;
        var key = args.Length > 2 ? args[2] : DefaultKey;

        if (!File.Exists(databasePath))
        {
            Logger.Print($"error: database not found: {databasePath}");
            return 2;
        }

        return Run(databasePath, entry, key);
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
