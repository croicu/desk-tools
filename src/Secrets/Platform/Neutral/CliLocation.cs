using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64 -- genuinely unset only
// happens now if src/Directory.Build.props' own win-x64 default gets explicitly cleared. See
// Platform/Linux/CliLocation.cs's own remarks for why this throws rather than guessing a path --
// same reasoning, just for "no RID specified" rather than "Linux specifically".
internal static class CliLocation
{
    internal static string Resolve(string? programFilesDir = null)
    {
        throw new AppError("locating keepassxc-cli is not supported on this platform yet -- see issue #50.", LogCategory.Secrets);
    }
}
