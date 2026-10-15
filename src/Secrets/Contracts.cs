namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Supplies the KeePass master password, one attempt at a time. <see cref="PromptedPassword"/> is
/// the real implementation (prompts, then holds the password until Service exits); tests use fakes.
/// </summary>
internal interface IPasswordSource
{
    /// <summary>
    /// The password to try for <paramref name="databasePath"/> on 0-based <paramref name="attempt"/>,
    /// or null if none is available (cancelled or closed). The caller zeroes the returned array.
    /// </summary>
    byte[]? Get(string databasePath, int attempt);

    /// <summary>The password last returned by <see cref="Get"/> was rejected; drop it if held.</summary>
    void Rejected();
}

/// <summary>
/// Asks the user for the KeePass master password -- the platform-specific part (a Windows dialog
/// today, see <c>Platform/</c>) behind <see cref="PromptedPassword"/>, which adds the holding
/// policy. Kept as an interface so that policy is unit-testable without a window.
/// </summary>
internal interface IPasswordPrompt
{
    /// <summary>
    /// Starts asking for the password for <paramref name="databasePath"/>, labelled "attempt
    /// <paramref name="attempt"/> + 1 of <paramref name="maxAttempts"/>" when the attempt is not the
    /// first, and returns at once. The task completes with the password as bytes the receiver zeroes,
    /// or null if the user cancelled -- <b>and never on a timer</b>: the prompt stays up until the user
    /// answers or <paramref name="cancellation"/> fires, which closes it (completing with null). Whoever
    /// is waiting for the answer decides how long it is willing to (issue #66; today a launch waits for as long as it takes). A failure to show the
    /// prompt faults the task rather than throwing here.
    /// </summary>
    Task<byte[]?> AskAsync(string databasePath, int attempt, int maxAttempts, CancellationToken cancellation);
}

/// <summary>
/// Shows the user a file-open dialog -- the platform-specific half of choosing the KeePass database
/// (a Windows common dialog today, see <c>Platform/</c>); <see cref="DatabaseLocation"/> adds the
/// "only when unset" policy and the validation. An interface so that policy is unit-testable without
/// a window.
/// </summary>
internal interface IFilePicker
{
    /// <summary>
    /// Asks for one existing file whose name ends in <paramref name="extension"/> (no leading dot,
    /// e.g. <c>"kdbx"</c>), described as <paramref name="filterName"/> in the type filter. Returns its
    /// full path, or null if the user cancelled.
    /// </summary>
    string? Pick(string title, string filterName, string extension);
}

/// <summary>
/// Where the chosen database path is remembered -- <see cref="DatabasePathStore"/> in production (the
/// settings files), a fake in tests.
/// </summary>
internal interface IDatabasePathStore
{
    /// <summary>Says where the value lives, for error messages that tell a developer where to look.</summary>
    string Description { get; }

    /// <summary>The saved path, or null if it is missing (or not a string).</summary>
    string? Get();

    /// <summary>Saves the path where <see cref="Get"/> will find it, keeping every unrelated setting.</summary>
    void Set(string path);
}
