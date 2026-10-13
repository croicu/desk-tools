using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

// Placeholder: mirrors Platform/Windows/CliLocation.cs's own API shape so callers need no
// #if/OperatingSystem.IsWindows() branch, same reasoning as src/Service/Platform/'s own split.
// Throws rather than guessing a path: this class decides which binary an *elevated* process
// executes, and a wrong guess (PATH lookup, a user-writable prefix) is exactly the mistake it
// exists to prevent. A real Linux implementation needs a deliberate answer for where a trusted,
// root-owned keepassxc-cli lives (distro package path, ownership/permission checks) -- tracked as
// its own task, see issue #50's "Linux" follow-up.
internal static class CliLocation
{
    internal static string Resolve(string? programFilesDir = null)
    {
        throw new AppError("locating keepassxc-cli is not supported on Linux yet -- see issue #50 (Linux follow-up).", LogCategory.Secrets);
    }
}
