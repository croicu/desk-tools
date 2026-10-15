using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The real <see cref="ISecretBroker"/>: finds the KeePass database (<see cref="DatabaseLocation"/>:
/// the saved path, or a file picker the first time), locates the trusted <c>keepassxc-cli</c>
/// (<see cref="CliLocation"/>) and reads through a <see cref="Vault"/> whose master password comes from
/// a <see cref="PromptedPassword"/> -- held for this broker's lifetime, so the dialog appears once per
/// Service run. Nothing is composed or looked up until the first <see cref="Read"/>: a Service on a
/// machine without KeePassXC, or with no tool that declares a secret, never notices any of this.
///
/// Disposing closes an open password dialog (releasing any launch waiting on it) and zeroes the held
/// password.
/// </summary>
public sealed class SecretBroker : ISecretBroker
{
    private readonly DatabaseLocation _location;
    private readonly Func<string> _cliPath;
    private readonly IProcessRunner _runner;
    private readonly IPasswordSource _passwords;
    private readonly IDisposable? _ownedPasswords;

    // Two launches needing a secret at the same moment must not open two file pickers when no
    // database is configured yet: the second waits for the first's answer, then finds it saved.
    private readonly object _locationLock = new();

    /// <summary>
    /// The production composition. Windows only today (its dialogs are Win32): a host guards the call
    /// with <c>OperatingSystem.IsWindows()</c> and runs without a broker elsewhere.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public SecretBroker()
        : this(
              new DatabaseLocation(new DatabasePathStore(), new FilePicker()),
              () => CliLocation.Resolve(),
              new ProcessRunner(),
              new PromptedPassword(new PasswordPrompt()))
    {
    }

    internal SecretBroker(DatabaseLocation location, Func<string> cliPath, IProcessRunner runner, IPasswordSource passwords)
    {
        _location = location;
        _cliPath = cliPath;
        _runner = runner;
        _passwords = passwords;
        _ownedPasswords = passwords as IDisposable;
    }

    public IReadOnlyDictionary<string, string> Read(string entry, IReadOnlyList<string> keys)
    {
        string databasePath;
        lock (_locationLock)
        {
            databasePath = _location.Resolve();
        }

        var vault = new Vault(databasePath, new KeePassCli(_runner, _cliPath()), _passwords);
        var raw = vault.Read(entry, keys);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in raw)
            {
                values[pair.Key] = Encoding.UTF8.GetString(pair.Value);
            }
        }
        finally
        {
            foreach (var buffer in raw.Values)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        return values;
    }

    public void Dispose()
    {
        _ownedPasswords?.Dispose();
    }
}
