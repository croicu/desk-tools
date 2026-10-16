using System.Security.Cryptography;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The real <see cref="IPasswordSource"/>: asks an <see cref="IPasswordPrompt"/> for the master
/// password and then <b>holds</b> it, so later launches in the same Service process don't ask again.
/// The policy (see issues #50 and #66):
/// <list type="bullet">
/// <item><b>The dialog never times out, and a launch that needs the password waits for the answer
/// however long it takes</b> ("if the secrets are not ready, wait for them"). <see cref="Get"/>
/// returns only when the user has answered, cancelled, or the source is disposed. A slow answer may
/// outlast the MCP client's own connect timeout, but the answer is held either way, so the next
/// attempt gets it at once.</item>
/// <item>One dialog at a time: a launch that needs the password while one is open joins it (same
/// database) and never opens a second; a request for a different database meanwhile gets null at once.</item>
/// <item>Held until the process exits (<see cref="Dispose"/>, which also closes any open dialog and so
/// releases every waiter) -- no TTL of its own; Service's idle exit is the bound.</item>
/// <item><see cref="Rejected"/> drops the held password, so the next attempt asks again; a held
/// password that stops working (changed in KeePassXC) therefore re-prompts instead of failing.</item>
/// <item>A cancelled dialog holds nothing and returns null (the user declined: the launch goes on
/// without the secret); a failure to show it is logged and treated the same way -- an optional
/// capability must never wedge a launch.</item>
/// <item>Only reused for the same database.</item>
/// <item><b>Remembered across runs</b> (issue #84), when an <see cref="IPasswordStore"/> is supplied: a password that
/// <see cref="Accepted"/> says KeePassXC actually accepted is saved, and the next run -- with nothing held yet --
/// loads it instead of asking. Until explicitly forgotten (<see cref="Forget"/>) or rejected
/// (<see cref="Rejected"/>: the password was changed in KeePassXC). A store that fails is a warning, never a
/// failed launch: the prompt just comes up as if nothing were remembered.</item>
/// </list>
/// The held password is a pinned byte array, zeroed when dropped. Callers get <i>copies</i>, so
/// <see cref="Vault"/> zeroing the one it was handed never wipes the held one.
/// </summary>
internal sealed class PromptedPassword : IPasswordSource, IDisposable
{
    private readonly IPasswordPrompt _prompt;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lock = new();

    private byte[]? _held;
    private string? _heldFor;

    private Task? _pending;
    private string? _pendingFor;

    private bool _disposed;

    // The remembered password: null means the feature is off (nothing is ever read or written).
    private readonly IPasswordStore? _store;

    // True once what is held is also in the store (loaded from it, or saved after being accepted).
    private bool _persisted;

    internal PromptedPassword(IPasswordPrompt prompt, IPasswordStore? store = null)
    {
        _prompt = prompt;
        _store = store;
    }

    public byte[]? Get(string databasePath, int attempt)
    {
        Task pending;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_held is not null && SameDatabase(_heldFor, databasePath))
            {
                Logger.Diagnostic($"secrets: using the held master password for '{databasePath}'.", LogCategory.Secrets);
                return Copy(_held);
            }

            if (_pending is null)
            {
                // Whatever was held is for another database; never hand it to this one.
                Drop();

                // First attempt only: a remembered password that KeePassXC then rejects is forgotten (Rejected), so
                // asking again after a failed attempt must not load the same bad one back.
                if (attempt == 0 && TryLoadRemembered(databasePath))
                {
                    return Copy(_held!);
                }

                OpenPrompt(databasePath, attempt);
            }
            else if (!SameDatabase(_pendingFor, databasePath))
            {
                Logger.Warning($"secrets: a master password prompt for '{_pendingFor}' is already open; not asking for '{databasePath}' meanwhile.", LogCategory.Secrets);
                return null;
            }

            // OpenPrompt leaves _pending null when the prompt answered synchronously (a fake in a test).
            if (_pending is null)
            {
                return TakeHeldCopy(databasePath);
            }

            pending = _pending;
        }

        // Until the user answers or cancels, or Dispose closes the dialog. Outside the lock: the task
        // that completes this wait needs the lock to store the answer.
        pending.Wait();

        lock (_lock)
        {
            return TakeHeldCopy(databasePath);
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

            // Whatever was remembered is the password that was just rejected (or one like it): it is wrong now.
            ForgetRemembered();
        }
    }

    public void Accepted()
    {
        lock (_lock)
        {
            if (_store is null || _held is null || _heldFor is null || _persisted)
            {
                return;
            }

            try
            {
                _store.Save(_heldFor, _held);
                _persisted = true;
                Logger.Info($"secrets: the master password for '{_heldFor}' was accepted; remembering it.", LogCategory.Secrets);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Logger.Warning($"secrets: could not remember the master password ({error.Message}); it is held for this run only.", LogCategory.Secrets);
            }
        }
    }

    public void Forget()
    {
        lock (_lock)
        {
            Drop();
            ForgetRemembered();
            Logger.Info("secrets: the master password was forgotten.", LogCategory.Secrets);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Drop();
        }

        // Closes a dialog that is still open (Service exiting must not leave an orphaned window). Done
        // outside the lock: cancellation completes the pending task, whose continuation takes the lock.
        _shutdown.Cancel();
    }

    /// <summary>
    /// Starts the dialog and a task that waits for its answer and holds it. Must be called with
    /// <see cref="_lock"/> held. <see cref="_pending"/> is only set if the answer has not already
    /// arrived: the waiting task itself clears it (under the same lock) when it finishes.
    /// </summary>
    private void OpenPrompt(string databasePath, int attempt)
    {
        var ask = _prompt.AskAsync(databasePath, attempt, Vault.MaxPasswordAttempts, _shutdown.Token);
        var holding = HoldAnswerAsync(ask, databasePath);

        if (!holding.IsCompleted)
        {
            _pending = holding;
            _pendingFor = databasePath;
        }
    }

    /// <summary>
    /// Waits for the dialog's answer -- however late -- and holds it. Never faults: a dialog that
    /// could not be shown is logged and counts as "no answer". Runs the continuation on a pool thread
    /// via <see cref="TaskCompletionSource{TResult}"/>'s own asynchronous completion, so it may block on
    /// <see cref="_lock"/> without ever being on a caller's stack.
    /// </summary>
    private async Task HoldAnswerAsync(Task<byte[]?> ask, string databasePath)
    {
        byte[]? answer = null;
        try
        {
            answer = await ask.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Warning($"secrets: the master password prompt for '{databasePath}' failed: {error.Message}", LogCategory.Secrets);
        }

        lock (_lock)
        {
            _pending = null;
            _pendingFor = null;

            if (answer is null)
            {
                return;
            }

            if (_disposed)
            {
                CryptographicOperations.ZeroMemory(answer);
                return;
            }

            _held = answer;
            _heldFor = databasePath;
            Logger.Info($"secrets: master password entered for '{databasePath}'; holding it.", LogCategory.Secrets);
        }
    }

    /// <summary>Loads the remembered password for <paramref name="databasePath"/> into the held slot. Must be called with <see cref="_lock"/> held. False if none (or the store failed).</summary>
    private bool TryLoadRemembered(string databasePath)
    {
        if (_store is null)
        {
            return false;
        }

        byte[]? remembered;
        try
        {
            remembered = _store.Load(databasePath);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Warning($"secrets: could not read the remembered master password ({error.Message}); asking instead.", LogCategory.Secrets);
            return false;
        }

        if (remembered is null)
        {
            return false;
        }

        // Pinned, like every held copy (the store's own array is the caller's to zero).
        _held = Copy(remembered);
        _heldFor = databasePath;
        _persisted = true;
        CryptographicOperations.ZeroMemory(remembered);
        Logger.Info($"secrets: using the remembered master password for '{databasePath}'.", LogCategory.Secrets);
        return true;
    }

    /// <summary>Deletes the remembered password, if the feature is on. Must be called with <see cref="_lock"/> held; never throws.</summary>
    private void ForgetRemembered()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _store.Forget();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Warning($"secrets: could not forget the remembered master password ({error.Message}).", LogCategory.Secrets);
        }
    }

    private byte[]? TakeHeldCopy(string databasePath)
    {
        return _held is not null && SameDatabase(_heldFor, databasePath) ? Copy(_held) : null;
    }

    private void Drop()
    {
        if (_held is not null)
        {
            CryptographicOperations.ZeroMemory(_held);
        }

        _held = null;
        _heldFor = null;
        _persisted = false;
    }

    private static bool SameDatabase(string? left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Copy(byte[] source)
    {
        var copy = GC.AllocateArray<byte>(source.Length, pinned: true);
        source.CopyTo(copy, 0);
        return copy;
    }
}
