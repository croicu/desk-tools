namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// This assembly's single log/error category (see CLAUDE.md's Logging section). Log secret names,
/// entry/key and outcomes under it -- never a value, the master password, or raw CLI output.
/// </summary>
internal static class LogCategory
{
    internal const string Secrets = "secrets";
}
