namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// What may be opened as the KeePass database: an absolute path to an existing, <b>local</b>
/// <c>.kdbx</c> file. Applied to the path the file picker returns <i>and</i> to the one saved in the
/// per-user settings file -- the saved one matters more: that file is writable by any process running
/// as the user, while the elevated Service is what opens the path. A UNC path (or a network drive)
/// would make the elevated process authenticate to whatever server it names, handing that server the
/// account's NTLM credentials, so those are refused outright rather than merely validated.
/// </summary>
internal static class DatabasePathRule
{
    internal const string Extension = ".kdbx";

    /// <summary>The first thing wrong with <paramref name="path"/>, or null if it is acceptable.</summary>
    internal static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "the path is empty.";
        }

        // Checked on the text as given, before any normalization: GetFullPath would happily turn a UNC
        // path into a full path.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return "network (UNC) paths are not allowed: opening one from the elevated Service would send its credentials to that server.";
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return "the path is not absolute.";
        }

        var fullPath = Path.GetFullPath(path);

        if (!string.Equals(Path.GetExtension(fullPath), Extension, StringComparison.OrdinalIgnoreCase))
        {
            return $"expected a KeePass database file ending in '{Extension}'.";
        }

        if (IsNetworkDrive(fullPath))
        {
            return "files on a network drive are not allowed: opening one from the elevated Service would send its credentials to that server.";
        }

        if (!File.Exists(fullPath))
        {
            return "no such file.";
        }

        return null;
    }

    private static bool IsNetworkDrive(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            // Not a drive DriveInfo understands (e.g. a plain Unix root); nothing to refuse here.
            return false;
        }
    }
}
