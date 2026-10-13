using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class KeePassCliTests
{
    private const string Exe = @"C:\Program Files\KeePassXC\keepassxc-cli.exe";
    private const string Db = @"C:\data\my.kdbx";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void ReadNotes_Success_ReturnsNotesAndUsesTheExpectedCommandLine()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "desk-pager: abc\r\n");
        var cli = new KeePassCli(runner, Exe);

        var read = cli.ReadNotes(Db, "ntfy", Bytes("pw"));

        Assert.AreEqual(ReadStatus.Ok, read.Status);
        Assert.AreEqual("desk-pager: abc\r\n", Encoding.UTF8.GetString(read.Notes));
        Assert.HasCount(1, runner.Calls);
        Assert.AreEqual(Exe, runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "show", "-q", "-a", "Notes", Db, "ntfy" }, runner.Calls[0].Args);
    }

    [TestMethod]
    public void ReadNotes_Password_GoesOnStdinWithNewlineAndNeverOnTheCommandLine()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: v");
        var cli = new KeePassCli(runner, Exe);

        cli.ReadNotes(Db, "ntfy", Bytes("hunter2"));

        CollectionAssert.AreEqual(Bytes("hunter2\n"), runner.Calls[0].Stdin);
        foreach (var arg in runner.Calls[0].Args)
        {
            Assert.DoesNotContain("hunter2", arg);
        }
    }

    [TestMethod]
    public void ReadNotes_ExitOneWithEmptyStderr_IsAuthenticationFailed()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(1);
        var cli = new KeePassCli(runner, Exe);

        var read = cli.ReadNotes(Db, "ntfy", Bytes("wrong"));

        Assert.AreEqual(ReadStatus.AuthenticationFailed, read.Status);
        Assert.IsEmpty(read.Notes);
    }

    [TestMethod]
    public void ReadNotes_ExitOneWithStderr_IsFailedWithSummary()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(1, stderr: "Could not find entry with path nosuch.\r\n");
        var cli = new KeePassCli(runner, Exe);

        var read = cli.ReadNotes(Db, "nosuch", Bytes("pw"));

        Assert.AreEqual(ReadStatus.Failed, read.Status);
        Assert.AreEqual("exit code 1: Could not find entry with path nosuch.", read.Detail);
    }

    [TestMethod]
    public void ReadNotes_NonZeroExit_ZeroesWhateverStdoutHeld()
    {
        var stdout = Bytes("desk-pager: leaked");
        var runner = new StubRunner(new ProcessResult(1, stdout, "boom"));
        var cli = new KeePassCli(runner, Exe);

        cli.ReadNotes(Db, "ntfy", Bytes("pw"));

        CollectionAssert.AreEqual(new byte[stdout.Length], stdout);
    }

    [TestMethod]
    public void ReadNotes_EntryStartingWithDash_ThrowsBeforeRunningAnything()
    {
        var runner = new FakeProcessRunner();
        var cli = new KeePassCli(runner, Exe);

        Assert.ThrowsExactly<AppError>(() => cli.ReadNotes(Db, "--help", Bytes("pw")));
        Assert.IsEmpty(runner.Calls);
    }

    [TestMethod]
    public void Summarize_LongStderr_IsTruncatedToFirstLine()
    {
        var summary = KeePassCli.Summarize(2, new string('x', 500) + "\nsecond line");

        Assert.IsLessThan(KeePassCli.SummaryLimit + 40, summary.Length);
        Assert.DoesNotContain("second line", summary);
        Assert.EndsWith("...", summary);
    }

    [TestMethod]
    public void Summarize_EmptyStderr_IsJustTheExitCode()
    {
        Assert.AreEqual("exit code 7", KeePassCli.Summarize(7, string.Empty));
    }

    /// <summary>Returns one canned result holding a caller-owned stdout array.</summary>
    private sealed class StubRunner : IProcessRunner
    {
        private readonly ProcessResult _result;

        public StubRunner(ProcessResult result)
        {
            _result = result;
        }

        public ProcessResult Run(string fileName, IReadOnlyList<string> args, ReadOnlySpan<byte> stdin) => _result;
    }
}
