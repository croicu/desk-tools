namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Result of <see cref="KeePassCli.ReadNotes"/>. <see cref="Notes"/> holds the raw Notes bytes only
/// when <see cref="Status"/> is <see cref="ReadStatus.Ok"/> (empty otherwise) and the caller zeroes
/// it. <see cref="Detail"/> is a short, secret-free summary for an error message.
/// </summary>
internal sealed record NotesRead(ReadStatus Status, byte[] Notes, string Detail);
