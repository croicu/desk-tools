using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Placeholder: mirrors Platform/Windows/FilePicker.cs's own API shape so callers need no
// #if/OperatingSystem.IsWindows() branch, same reasoning as Platform/*/PasswordPrompt.cs. Throws
// rather than returning "cancelled": a caller that needs a database path must learn the platform
// can't ask for one. A real implementation (a desktop dialog, or a plain path prompt) is tracked in
// issue #51.
internal sealed class FilePicker : IFilePicker
{
    public string? Pick(string title, string filterName, string extension)
    {
        throw new AppError("asking for a file is not supported on Linux yet -- see issue #51.", LogCategory.Secrets);
    }
}
