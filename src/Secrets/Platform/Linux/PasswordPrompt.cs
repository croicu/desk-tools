using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Placeholder: mirrors Platform/Windows/PasswordPrompt.cs's own API shape (same constructor and
// Ask signature) so callers need no #if/OperatingSystem.IsWindows() branch, same reasoning as
// Platform/Linux/CliLocation.cs. Throws rather than silently returning "cancelled": a caller that
// needs a password must learn the platform can't ask for one. A real Linux implementation (a
// terminal prompt, or a desktop dialog) is tracked in issue #51.
internal sealed class PasswordPrompt : IPasswordPrompt
{
    internal PasswordPrompt(TimeSpan? timeout = null)
    {
    }

    public byte[]? Ask(string databasePath, int attempt, int maxAttempts)
    {
        throw new AppError("asking for the master password is not supported on Linux yet -- see issue #51.", LogCategory.Secrets);
    }
}
