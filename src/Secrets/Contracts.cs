namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Supplies the KeePass master password, one attempt at a time. Slice 1 only has fakes; the real
/// implementation is the elevated prompt (a later slice), which also decides how long a good
/// password is held.
/// </summary>
internal interface IPasswordSource
{
    /// <summary>
    /// The password to try for <paramref name="databasePath"/> on 0-based <paramref name="attempt"/>,
    /// or null if none is available (cancelled, timed out). The caller zeroes the returned array.
    /// </summary>
    byte[]? Get(string databasePath, int attempt);

    /// <summary>The password last returned by <see cref="Get"/> was rejected; drop it if held.</summary>
    void Rejected();
}
