using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class NotesParserTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void FindValue_SingleLine_ReturnsValue()
    {
        var value = NotesParser.FindValue(Bytes("desk-pager: abc-123"), "desk-pager");

        Assert.AreEqual("abc-123", Encoding.UTF8.GetString(value));
    }

    [TestMethod]
    public void FindValue_CrLfLines_ReturnsValueWithoutCarriageReturn()
    {
        var value = NotesParser.FindValue(Bytes("other: x\r\ndesk-pager: abc-123\r\nlast: y\r\n"), "desk-pager");

        Assert.AreEqual("abc-123", Encoding.UTF8.GetString(value));
    }

    [TestMethod]
    public void FindValue_SurroundingWhitespace_IsTrimmed()
    {
        var value = NotesParser.FindValue(Bytes("desk-pager:   abc-123  \t"), "desk-pager");

        Assert.AreEqual("abc-123", Encoding.UTF8.GetString(value));
    }

    [TestMethod]
    public void FindValue_ValueContainingColon_KeepsItWhole()
    {
        var value = NotesParser.FindValue(Bytes("url: https://example.test:8443/x"), "url");

        Assert.AreEqual("https://example.test:8443/x", Encoding.UTF8.GetString(value));
    }

    [TestMethod]
    public void FindValue_KeyIsPrefixOfAnotherKey_DoesNotMatchIt()
    {
        var error = Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("desk-pager: abc"), "desk"));

        Assert.Contains("'desk'", error.Message);
    }

    [TestMethod]
    public void FindValue_KeyIsCaseSensitive()
    {
        Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("Desk-Pager: abc"), "desk-pager"));
    }

    [TestMethod]
    public void FindValue_MissingKey_ThrowsNamingTheKey()
    {
        var error = Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("other: x"), "desk-pager"));

        Assert.Contains("'desk-pager'", error.Message);
        Assert.DoesNotContain("other: x", error.Message);
    }

    [TestMethod]
    public void FindValue_KeyOnTwoLines_ThrowsWithoutLeakingEitherValue()
    {
        var error = Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("k: first-secret\nk: second-secret"), "k"));

        Assert.Contains("'k'", error.Message);
        Assert.DoesNotContain("first-secret", error.Message);
        Assert.DoesNotContain("second-secret", error.Message);
    }

    [TestMethod]
    public void FindValue_EmptyValue_Throws()
    {
        var error = Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("k:   \nother: x"), "k"));

        Assert.Contains("empty", error.Message);
    }

    [TestMethod]
    public void FindValue_NoTrailingNewline_Works()
    {
        var value = NotesParser.FindValue(Bytes("a: 1\nk: last"), "k");

        Assert.AreEqual("last", Encoding.UTF8.GetString(value));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("a:b")]
    [DataRow("a\nb")]
    public void FindValue_InvalidKey_Throws(string key)
    {
        Assert.ThrowsExactly<AppError>(() => NotesParser.FindValue(Bytes("a: 1"), key));
    }
}
