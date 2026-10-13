namespace Croicu.Desk.Tools.Secrets;

/// <summary>How a <see cref="KeePassCli"/> read ended.</summary>
internal enum ReadStatus
{
    /// <summary>The entry's Notes were read.</summary>
    Ok,

    /// <summary>The master password was rejected -- worth asking for another.</summary>
    AuthenticationFailed,

    /// <summary>Anything else (entry not found, unreadable database, ...) -- retrying won't help.</summary>
    Failed,
}
