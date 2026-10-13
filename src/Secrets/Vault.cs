using System.Security.Cryptography;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Reads secrets out of one KeePass database: asks the <see cref="IPasswordSource"/> for the master
/// password (up to <see cref="MaxPasswordAttempts"/> times), has <see cref="KeePassCli"/> read an
/// entry's Notes with it, and pulls the requested <c>key: value</c> lines out of those. A broker, not
/// a store: nothing is cached, so every call sees the database's current contents and rotation needs
/// no invalidation.
///
/// Returned values are pinned UTF-8 byte arrays the caller zeroes after use. The only place a value
/// must become a <see cref="string"/> is the launched tool's environment block, which is the
/// delivery mechanism (see issue #50); that last copy can't be wiped, and is accepted.
/// </summary>
internal sealed class Vault
{
    internal const int MaxPasswordAttempts = 3;

    private readonly string _databasePath;
    private readonly KeePassCli _cli;
    private readonly IPasswordSource _passwords;

    internal Vault(string databasePath, KeePassCli cli, IPasswordSource passwords)
    {
        _databasePath = databasePath;
        _cli = cli;
        _passwords = passwords;
    }

    /// <summary>
    /// Reads every key in <paramref name="keys"/> from <paramref name="entry"/> with a single CLI
    /// call (one key derivation however many keys), returning key -> value. Throws
    /// <see cref="AppError"/> naming the entry and key (never a value) when the entry or a key is
    /// missing, when no password is available, or when the password is rejected
    /// <see cref="MaxPasswordAttempts"/> times. On any failure nothing is returned and every buffer
    /// already filled is zeroed.
    /// </summary>
    internal IReadOnlyDictionary<string, byte[]> Read(string entry, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            throw new AppError($"no keys requested from KeePass entry '{entry}'.", LogCategory.Secrets);
        }

        Logger.Diagnostic($"secrets: reading {keys.Count} key(s) from entry '{entry}'.", LogCategory.Secrets);

        var notes = ReadNotesWithPassword(entry);
        try
        {
            var values = ExtractValues(entry, keys, notes);
            Logger.Info($"secrets: read {values.Count} key(s) from entry '{entry}'.", LogCategory.Secrets);
            return values;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(notes);
        }
    }

    private byte[] ReadNotesWithPassword(string entry)
    {
        for (var attempt = 0; attempt < MaxPasswordAttempts; attempt++)
        {
            var password = _passwords.Get(_databasePath, attempt);
            if (password is null)
            {
                throw new AppError(
                    $"no master password available for '{_databasePath}' (attempt {attempt + 1} of {MaxPasswordAttempts}): the prompt was cancelled or timed out.",
                    LogCategory.Secrets);
            }

            NotesRead read;
            try
            {
                read = _cli.ReadNotes(_databasePath, entry, password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }

            switch (read.Status)
            {
                case ReadStatus.Ok:
                    return read.Notes;

                case ReadStatus.AuthenticationFailed:
                    Logger.Warning($"secrets: master password rejected for '{_databasePath}' (attempt {attempt + 1} of {MaxPasswordAttempts}).", LogCategory.Secrets);
                    _passwords.Rejected();
                    break;

                default:
                    throw new AppError(
                        $"keepassxc-cli could not read entry '{entry}' from '{_databasePath}' ({read.Detail}).",
                        LogCategory.Secrets);
            }
        }

        throw new AppError(
            $"master password rejected {MaxPasswordAttempts} times for '{_databasePath}'; giving up.",
            LogCategory.Secrets);
    }

    private static Dictionary<string, byte[]> ExtractValues(string entry, IReadOnlyList<string> keys, byte[] notes)
    {
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var key in keys)
            {
                if (values.ContainsKey(key))
                {
                    throw new AppError($"key '{key}' requested twice from KeePass entry '{entry}'.", LogCategory.Secrets);
                }

                try
                {
                    values[key] = NotesParser.FindValue(notes, key);
                }
                catch (AppError error)
                {
                    throw new AppError($"KeePass entry '{entry}': {error.Message}", LogCategory.Secrets);
                }
            }
        }
        catch
        {
            foreach (var value in values.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            throw;
        }

        return values;
    }
}
