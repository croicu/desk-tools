namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Supplies the KeePass master password, one attempt at a time. <see cref="PromptedPassword"/> is
/// the real implementation (prompts, then holds the password until Service exits); tests use fakes.
/// </summary>
internal interface IPasswordSource
{
    /// <summary>
    /// The password to try for <paramref name="databasePath"/> on 0-based <paramref name="attempt"/>,
    /// or null if none is available (cancelled, timed out). The caller zeroes the returned array.
    /// </summary>
    byte[]? Get(string databasePath, int attempt);

    /// <summary>The password last returned by <see cref="Get"/> was rejected; drop it if held.</summary>
    void Rejected();
}

/// <summary>
/// Asks the user for the KeePass master password -- the platform-specific part (a Windows dialog
/// today, see <c>Platform/</c>) behind <see cref="PromptedPassword"/>, which adds the holding and
/// re-prompt policy. Kept as an interface so that policy is unit-testable without a window.
/// </summary>
internal interface IPasswordPrompt
{
    /// <summary>
    /// Shows the prompt for <paramref name="databasePath"/>, labelled "attempt
    /// <paramref name="attempt"/> + 1 of <paramref name="maxAttempts"/>" when the attempt is not the
    /// first. Returns the password as bytes the caller zeroes, or null if the user cancelled or the
    /// prompt timed out.
    /// </summary>
    byte[]? Ask(string databasePath, int attempt, int maxAttempts);
}
