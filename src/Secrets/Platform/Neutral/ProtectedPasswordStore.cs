using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Placeholder: mirrors Platform/Windows/ProtectedPasswordStore.cs's own API shape so callers need no
// #if/OperatingSystem.IsWindows() branch, same reasoning as the other Platform/ placeholders. Remembering the master
// password needs a platform facility that keeps it from every unelevated process (DPAPI machine scope plus an
// admin-only directory on Windows); there is no equivalent decided for Linux yet -- see issue #51.
internal sealed class ProtectedPasswordStore : IPasswordStore
{
    internal ProtectedPasswordStore(string? directory = null)
    {
    }

    public byte[]? Load(string databasePath)
    {
        throw new AppError("remembering the master password is not supported on this platform yet -- see issue #51.", LogCategory.Secrets);
    }

    public void Save(string databasePath, byte[] password)
    {
        throw new AppError("remembering the master password is not supported on this platform yet -- see issue #51.", LogCategory.Secrets);
    }

    public void Forget()
    {
        throw new AppError("remembering the master password is not supported on this platform yet -- see issue #51.", LogCategory.Secrets);
    }
}
