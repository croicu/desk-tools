using System.Security.Cryptography;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The real <see cref="IPasswordSource"/>: asks an <see cref="IPasswordPrompt"/> for the master
/// password and then <b>holds</b> it, so later launches in the same Service process don't ask again.
/// The policy (see issue #50):
/// <list type="bullet">
/// <item>Held until the process exits (<see cref="Dispose"/>) -- no TTL of its own; Service's idle
/// exit is the bound.</item>
/// <item><see cref="Rejected"/> drops the held password, so the next attempt asks again; a held
/// password that stops working (changed in KeePassXC) therefore re-prompts instead of failing.</item>
/// <item>A cancel or timeout clears the held password and returns null (the launch fails).</item>
/// <item>Only reused for the same database; a different one prompts.</item>
/// <item>One dialog at a time: a second caller waits, then reuses whatever the first entered.</item>
/// </list>
/// The held password is a pinned byte array, zeroed when dropped. Callers get <i>copies</i>, so
/// <see cref="Vault"/> zeroing the one it was handed never wipes the held one.
/// </summary>
internal sealed class PromptedPassword : IPasswordSource, IDisposable
{
    private readonly IPasswordPrompt _prompt;
    private readonly object _lock = new();

    private byte[]? _held;
    private string? _heldFor;
    private bool _disposed;

    internal PromptedPassword(IPasswordPrompt prompt)
    {
        _prompt = prompt;
    }

    public byte[]? Get(string databasePath, int attempt)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_held is not null && string.Equals(_heldFor, databasePath, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Diagnostic($"secrets: using the held master password for '{databasePath}'.", LogCategory.Secrets);
                return Copy(_held);
            }

            // Whatever was held is for another database; never hand it to this one.
            Drop();

            var entered = _prompt.Ask(databasePath, attempt, Vault.MaxPasswordAttempts);
            if (entered is null)
            {
                Logger.Info($"secrets: no master password entered for '{databasePath}' (cancelled or timed out).", LogCategory.Secrets);
                return null;
            }

            _held = entered;
            _heldFor = databasePath;
            return Copy(entered);
        }
    }

    public void Rejected()
    {
        lock (_lock)
        {
            if (_held is not null)
            {
                Logger.Info($"secrets: dropping the held master password for '{_heldFor}' (rejected).", LogCategory.Secrets);
            }

            Drop();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Drop();
        }
    }

    private void Drop()
    {
        if (_held is not null)
        {
            CryptographicOperations.ZeroMemory(_held);
        }

        _held = null;
        _heldFor = null;
    }

    private static byte[] Copy(byte[] source)
    {
        var copy = GC.AllocateArray<byte>(source.Length, pinned: true);
        source.CopyTo(copy, 0);
        return copy;
    }
}
