using System.Text;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Only compiled in for win-x64 (see tests/Secrets/Secrets.Tests.csproj's Platform/ selection).
/// Walks the built template the way Windows' dialog manager will -- header, then each item on a DWORD
/// boundary -- so a layout mistake fails here as a plain assertion instead of as a dialog that
/// silently fails to appear.
/// </summary>
[TestClass]
public sealed class DialogTemplateTests
{
    private const string Db = @"C:\Users\someone\OneDrive\Secrets.kdbx";

    private sealed record Item(uint Style, int Id, ushort WindowClass, string Text);

    private static string ReadWide(BinaryReader reader)
    {
        var text = new StringBuilder();
        ushort character;
        while ((character = reader.ReadUInt16()) != 0)
        {
            text.Append((char)character);
        }

        return text.ToString();
    }

    /// <summary>Parses header + items; asserts the layout is exactly consumed.</summary>
    private static (uint Style, uint ExStyle, string Title, List<Item> Items) Parse(byte[] template)
    {
        using var stream = new MemoryStream(template);
        using var reader = new BinaryReader(stream, Encoding.Unicode);

        var style = reader.ReadUInt32();
        var exStyle = reader.ReadUInt32();
        var count = reader.ReadUInt16();
        reader.ReadBytes(8); // x, y, cx, cy
        Assert.AreEqual(0, reader.ReadUInt16(), "menu");
        Assert.AreEqual(0, reader.ReadUInt16(), "class");
        var title = ReadWide(reader);
        reader.ReadUInt16(); // font point size
        ReadWide(reader); // font face

        var items = new List<Item>();
        for (var i = 0; i < count; i++)
        {
            while (stream.Position % 4 != 0)
            {
                stream.ReadByte();
            }

            var itemStyle = reader.ReadUInt32();
            reader.ReadUInt32(); // extended style
            reader.ReadBytes(8); // x, y, cx, cy
            var id = reader.ReadUInt16();
            Assert.AreEqual(0xFFFF, reader.ReadUInt16(), "class marker");
            var windowClass = reader.ReadUInt16();
            var text = ReadWide(reader);
            Assert.AreEqual(0, reader.ReadUInt16(), "creation data");
            items.Add(new Item(itemStyle, id, windowClass, text));
        }

        Assert.AreEqual(template.Length, stream.Position, "trailing bytes after the last item.");
        return (style, exStyle, title, items);
    }

    [TestMethod]
    public void Build_FirstAttempt_HasFiveControlsAndNoRetryLine()
    {
        var (_, _, _, items) = Parse(DialogTemplate.Build(Db, attempt: 0, maxAttempts: 3));

        CollectionAssert.AreEqual(
            new[] { DialogTemplate.IdPrompt, DialogTemplate.IdPath, DialogTemplate.IdPassword, DialogTemplate.IdOk, DialogTemplate.IdCancel },
            items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void Build_Retry_AddsTheAttemptLine()
    {
        var (_, _, _, items) = Parse(DialogTemplate.Build(Db, attempt: 1, maxAttempts: 3));

        var line = items.Single(item => item.Id == DialogTemplate.IdAttempt);
        Assert.AreEqual("Incorrect password. Attempt 2 of 3.", line.Text);
        Assert.HasCount(6, items);
    }

    [TestMethod]
    public void Build_ShowsTheDatabasePath()
    {
        var (_, _, _, items) = Parse(DialogTemplate.Build(Db, 0, 3));

        Assert.AreEqual(Db, items.Single(item => item.Id == DialogTemplate.IdPath).Text);
    }

    [TestMethod]
    public void Build_PasswordEdit_IsMaskedAndTheDialogIsVisibleAndTopmost()
    {
        var (style, exStyle, title, items) = Parse(DialogTemplate.Build(Db, 0, 3));

        const uint esPassword = 0x20;
        const uint wsVisible = 0x10000000;
        const uint wsExTopmost = 0x8;
        var edit = items.Single(item => item.Id == DialogTemplate.IdPassword);
        Assert.AreNotEqual(0u, edit.Style & esPassword, "the edit box must be ES_PASSWORD.");
        Assert.AreEqual(0x0081, edit.WindowClass);
        Assert.AreNotEqual(0u, style & wsVisible);
        Assert.AreNotEqual(0u, exStyle & wsExTopmost);
        Assert.AreEqual(DialogTemplate.Title, title);
    }

    [TestMethod]
    public void Build_OkIsTheDefaultButton()
    {
        var (_, _, _, items) = Parse(DialogTemplate.Build(Db, 0, 3));

        const uint bsDefPushButton = 0x1;
        Assert.AreNotEqual(0u, items.Single(item => item.Id == DialogTemplate.IdOk).Style & bsDefPushButton);
        Assert.AreEqual(0u, items.Single(item => item.Id == DialogTemplate.IdCancel).Style & bsDefPushButton);
    }
}
