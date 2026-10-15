using System.Text;

namespace Croicu.Desk.Tools.Service.Launcher.Tests.Unit;

/// <summary>
/// <see cref="ToolConnection"/> over real pipes (see <see cref="FakeToolPipes"/>): the framing the MCP stdio transport
/// needs (newline-delimited UTF-8, no byte-order mark, flushed on every write), and that disposing it is how the
/// tool learns its client is gone.
/// </summary>
[TestClass]
public sealed class ToolConnectionTests
{
    [TestMethod]
    public void Input_WritesNewlineDelimitedUtf8WithoutABom_FlushedAtOnce()
    {
        using var pipes = new FakeToolPipes();
        using var connection = ToolConnection.FromHandles(pipes.StdinHandle, pipes.StdoutHandle);

        connection.Input.WriteLine("héllo ✅");

        // ReadLine returning at all proves it was flushed (the pipe is otherwise silent); no BOM means the first
        // character is the first one written.
        Assert.AreEqual("héllo ✅", pipes.ToolStdin.ReadLine());
    }

    [TestMethod]
    public void Output_ReadsTheToolsLines_AndEndsWhenTheToolDoes()
    {
        using var pipes = new FakeToolPipes();
        using var connection = ToolConnection.FromHandles(pipes.StdinHandle, pipes.StdoutHandle);

        pipes.ToolStdout.WriteLine("one");
        pipes.ToolStdout.WriteLine("two");
        pipes.Die();

        Assert.AreEqual("one", connection.Output.ReadLine());
        Assert.AreEqual("two", connection.Output.ReadLine());
        Assert.IsNull(connection.Output.ReadLine());
    }

    [TestMethod]
    public void Dispose_ClosesTheToolsStdin_SoTheToolSeesEndOfInput()
    {
        using var pipes = new FakeToolPipes();
        var connection = ToolConnection.FromHandles(pipes.StdinHandle, pipes.StdoutHandle);

        connection.Dispose();

        Assert.IsNull(pipes.ToolStdin.ReadLine());
    }

    [TestMethod]
    public void Dispose_WhenTheToolIsAlreadyGone_DoesNotThrow()
    {
        var pipes = new FakeToolPipes();
        var connection = ToolConnection.FromHandles(pipes.StdinHandle, pipes.StdoutHandle);

        // The tool vanished: nothing reads its stdin any more, so flushing/closing our end hits a broken pipe.
        pipes.ToolStdin.Dispose();
        pipes.Die();

        connection.Dispose();
    }

    [TestMethod]
    public void PublicConstructor_WrapsAnyTwoStreams()
    {
        var input = new StringWriter();
        using var connection = new ToolConnection(input, new StringReader("hello\n"));

        connection.Input.Write("x");

        Assert.AreEqual("x", input.ToString());
        Assert.AreEqual("hello", connection.Output.ReadLine());
        Assert.IsNotNull(Encoding.UTF8);
    }
}
