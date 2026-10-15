using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64. See
// Platform/Linux/PasswordPrompt.cs's own remarks for why this throws -- same reasoning, just for
// "no RID specified" rather than "Linux specifically".
internal sealed class PasswordPrompt : IPasswordPrompt
{
    public Task<byte[]?> AskAsync(string databasePath, int attempt, int maxAttempts, CancellationToken cancellation)
    {
        throw new AppError("asking for the master password is not supported on this platform yet -- see issue #53.", LogCategory.Secrets);
    }
}
