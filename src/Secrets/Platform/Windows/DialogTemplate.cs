using System.Text;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Builds the in-memory <c>DLGTEMPLATE</c> that <c>DialogBoxIndirectParam</c> turns into the
/// master-password dialog (see <see cref="PasswordPrompt"/>): title, "enter the password to open",
/// the database path (path-ellipsized), an "incorrect password, attempt N of M" line on re-tries, a
/// masked edit box, OK and Cancel. Pure byte layout, no Win32 calls, so it is unit-testable.
///
/// Layout facts that are easy to get wrong: the header is a <c>DLGTEMPLATE</c> (not the EX form)
/// followed by a menu word, a class word, the title, then -- because of <c>DS_SETFONT</c> -- a point
/// size and font name; every item then starts on a DWORD boundary and is a <c>DLGITEMTEMPLATE</c>, a
/// class given as <c>0xFFFF</c> + atom, a title string, and a zero-length creation-data word.
/// </summary>
internal static class DialogTemplate
{
    internal const int IdOk = 1;
    internal const int IdCancel = 2;
    internal const int IdPrompt = 100;
    internal const int IdPath = 101;
    internal const int IdAttempt = 102;
    internal const int IdPassword = 103;

    internal const string Title = "Desk Tools - KeePass master password";
    internal const string PromptText = "Enter the master password to open this KeePass database:";

    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsTabStop = 0x00010000;
    private const uint WsBorder = 0x00800000;
    private const uint DsModalFrame = 0x0080;
    private const uint DsSetFont = 0x0040;
    private const uint DsCenter = 0x0800;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExAppWindow = 0x00040000;
    private const uint EsPassword = 0x0020;
    private const uint EsAutoHScroll = 0x0080;
    private const uint BsDefPushButton = 0x0001;
    private const uint SsNoPrefix = 0x0080;
    private const uint SsPathEllipsis = 0x8000;

    private const ushort ClassButton = 0x0080;
    private const ushort ClassEdit = 0x0081;
    private const ushort ClassStatic = 0x0082;

    /// <summary>
    /// The dialog for <paramref name="databasePath"/>. When <paramref name="attempt"/> is above zero
    /// an extra line says the previous password was wrong and which attempt this is.
    /// </summary>
    internal static byte[] Build(string databasePath, int attempt, int maxAttempts)
    {
        var retry = attempt > 0;
        var itemCount = retry ? 6 : 5;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);

        // DLGTEMPLATE
        writer.Write(WsPopup | WsCaption | WsSysMenu | WsVisible | DsModalFrame | DsSetFont | DsCenter);
        writer.Write(WsExTopmost | WsExAppWindow);
        writer.Write((ushort)itemCount);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)250);
        writer.Write((short)(retry ? 112 : 98));
        writer.Write((ushort)0); // no menu
        writer.Write((ushort)0); // default dialog class
        WriteWide(writer, Title);
        writer.Write((ushort)9); // DS_SETFONT: point size, then face name
        WriteWide(writer, "Segoe UI");

        var y = 8;
        AddItem(writer, WsChild | WsVisible, 10, y, 230, 10, IdPrompt, ClassStatic, PromptText);
        y += 14;
        AddItem(writer, WsChild | WsVisible | SsNoPrefix | SsPathEllipsis, 10, y, 230, 10, IdPath, ClassStatic, databasePath);
        y += 16;

        if (retry)
        {
            AddItem(writer, WsChild | WsVisible, 10, y, 230, 10, IdAttempt, ClassStatic, $"Incorrect password. Attempt {attempt + 1} of {maxAttempts}.");
            y += 16;
        }

        AddItem(writer, WsChild | WsVisible | WsTabStop | WsBorder | EsPassword | EsAutoHScroll, 10, y, 230, 14, IdPassword, ClassEdit, string.Empty);
        y += 26;
        AddItem(writer, WsChild | WsVisible | WsTabStop | BsDefPushButton, 130, y, 50, 14, IdOk, ClassButton, "OK");
        AddItem(writer, WsChild | WsVisible | WsTabStop, 190, y, 50, 14, IdCancel, ClassButton, "Cancel");

        writer.Flush();
        return stream.ToArray();
    }

    private static void AddItem(BinaryWriter writer, uint style, int x, int y, int cx, int cy, int id, ushort windowClass, string text)
    {
        Align(writer);

        // DLGITEMTEMPLATE
        writer.Write(style);
        writer.Write(0u);
        writer.Write((short)x);
        writer.Write((short)y);
        writer.Write((short)cx);
        writer.Write((short)cy);
        writer.Write((ushort)id);
        writer.Write((ushort)0xFFFF);
        writer.Write(windowClass);
        WriteWide(writer, text);
        writer.Write((ushort)0); // no creation data
    }

    private static void WriteWide(BinaryWriter writer, string text)
    {
        foreach (var character in text)
        {
            writer.Write((ushort)character);
        }

        writer.Write((ushort)0);
    }

    private static void Align(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0)
        {
            writer.Write((byte)0);
        }
    }
}
