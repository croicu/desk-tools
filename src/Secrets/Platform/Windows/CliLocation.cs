using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Where <c>keepassxc-cli</c> may be run from. An elevated process must never execute a binary a
/// medium-integrity process could have replaced, so the path comes from the machine-wide Program
/// Files install, never from user-writable configuration, the PATH, or an environment variable.
///
/// Lives under <c>Platform/Windows/</c> (mirroring <c>src/Service/Platform/</c>'s split) because the
/// Program Files location, the <c>.exe</c> name and the case-insensitive path comparison are all
/// Windows facts -- see <c>Platform/Linux/CliLocation.cs</c>/<c>Platform/Neutral/CliLocation.cs</c>,
/// placeholders that throw until a Linux implementation exists. Same flat
/// <c>Croicu.Desk.Tools.Secrets</c> namespace as <c>Platform/*</c> elsewhere in this repo, since
/// callers select a variant at build time, not by namespace.
/// </summary>
internal static class CliLocation
{
    internal const string RelativePath = @"KeePassXC\keepassxc-cli.exe";

    /// <summary>
    /// The absolute path to <c>keepassxc-cli.exe</c> under <paramref name="programFilesDir"/>
    /// (defaults to the OS's own Program Files location). Throws <see cref="AppError"/>, with a
    /// message a developer can act on, if that directory isn't known on this platform or the file is
    /// missing.
    /// </summary>
    internal static string Resolve(string? programFilesDir = null)
    {
        var root = programFilesDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(root))
        {
            throw new AppError("cannot locate keepassxc-cli: this platform has no Program Files directory. Only a machine-wide install is trusted, never PATH.", LogCategory.Secrets);
        }

        var path = Path.GetFullPath(Path.Combine(root, RelativePath));
        if (!IsUnder(path, root))
        {
            throw new AppError($"refusing keepassxc-cli path '{path}': it is not under '{root}'.", LogCategory.Secrets);
        }

        if (!File.Exists(path))
        {
            throw new AppError($"keepassxc-cli not found at '{path}'. Install KeePassXC machine-wide (for all users), not per-user.", LogCategory.Secrets);
        }

        return path;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> (after normalizing both).</summary>
    internal static bool IsUnder(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        // Case-insensitive on purpose: Windows (the only platform this is trusted on) is.
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
