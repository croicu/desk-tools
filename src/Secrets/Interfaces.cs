using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// What a host (Service) needs from the Secrets library: read some <c>key: value</c> lines out of a
/// KeePass entry. Everything else in this project is <c>internal</c>; this is the public surface
/// (Architecture convention 7). An interface so the host's own tests can inject a fake instead of
/// opening a real database.
/// </summary>
public interface ISecretBroker : IDisposable
{
    /// <summary>
    /// Reads every key in <paramref name="keys"/> from <paramref name="entry"/> with one database
    /// open, returning key -> value. May show the master-password dialog and block until the user
    /// answers (issue #66). Throws <see cref="AppError"/> -- whose message names the entry and key,
    /// never a value -- when the database, entry or key can't be read or the user cancels; nothing is
    /// returned in that case.
    ///
    /// The values are <see cref="string"/>s because the only thing a host can do with them is put them
    /// in a launched tool's environment block, which needs strings (issue #50); the byte buffers they
    /// were decoded from are zeroed.
    /// </summary>
    IReadOnlyDictionary<string, string> Read(string entry, IReadOnlyList<string> keys);

    /// <summary>
    /// Forgets the master password: the one held in memory and the one remembered on disk (issue #84), so the next
    /// <see cref="Read"/> asks the user again. Not an error if there is none.
    /// </summary>
    void Forget();
}
