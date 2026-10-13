using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Finds <c>&lt;key&gt;: &lt;value&gt;</c> lines in a KeePass entry's Notes (the layout the user's
/// database already uses: entry <c>ntfy</c>, note line <c>desk-pager: ...</c>). Works on bytes, not
/// strings, so the surrounding text can be zeroed by the caller.
/// </summary>
internal static class NotesParser
{
    private const byte Colon = (byte)':';
    private const byte LineFeed = (byte)'\n';
    private const byte CarriageReturn = (byte)'\r';
    private const byte Space = (byte)' ';
    private const byte Tab = (byte)'\t';

    /// <summary>
    /// Returns the value of the single line <c>key: value</c> as a new pinned array (caller zeroes
    /// it). Lines end <c>\n</c> or <c>\r\n</c> (keepassxc-cli on Windows emits the latter); the key
    /// matches exactly (case-sensitive), then <c>:</c>, then any spaces/tabs are skipped and trailing
    /// whitespace dropped. Throws <see cref="AppError"/> naming the key (never a value) if the key is
    /// missing, appears on more than one line, or has an empty value.
    /// </summary>
    internal static byte[] FindValue(ReadOnlySpan<byte> notes, string key)
    {
        if (string.IsNullOrEmpty(key) || key.Contains(':') || key.Contains('\n') || key.Contains('\r'))
        {
            throw new AppError($"invalid secret key '{key}': must be non-empty and contain no ':' or line break.", LogCategory.Secrets);
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);

        var found = false;
        var valueStart = 0;
        var valueLength = 0;

        var lineStart = 0;
        while (lineStart <= notes.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < notes.Length && notes[lineEnd] != LineFeed)
            {
                lineEnd++;
            }

            var line = notes.Slice(lineStart, lineEnd - lineStart);
            if (TryMatch(line, keyBytes, out var offset, out var length))
            {
                if (found)
                {
                    throw new AppError($"secret key '{key}' appears on more than one line of the entry's Notes.", LogCategory.Secrets);
                }

                found = true;
                valueStart = lineStart + offset;
                valueLength = length;
            }

            lineStart = lineEnd + 1;
        }

        if (!found)
        {
            throw new AppError($"secret key '{key}' not found in the entry's Notes (expected a line '{key}: <value>').", LogCategory.Secrets);
        }

        if (valueLength == 0)
        {
            throw new AppError($"secret key '{key}' has an empty value in the entry's Notes.", LogCategory.Secrets);
        }

        var value = GC.AllocateArray<byte>(valueLength, pinned: true);
        notes.Slice(valueStart, valueLength).CopyTo(value);
        return value;
    }

    private static bool TryMatch(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        if (line.Length <= key.Length || !line.StartsWith(key) || line[key.Length] != Colon)
        {
            return false;
        }

        var start = key.Length + 1;
        while (start < line.Length && (line[start] == Space || line[start] == Tab))
        {
            start++;
        }

        var end = line.Length;
        while (end > start && (line[end - 1] == CarriageReturn || line[end - 1] == Space || line[end - 1] == Tab))
        {
            end--;
        }

        offset = start;
        length = end - start;
        return true;
    }
}
