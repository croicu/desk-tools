using System.Text;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class PromptedPasswordTests
{
    private const string Db = @"C:\data\my.kdbx";
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    [TestMethod]
    public void Get_FirstCall_PromptsAndReturnsWhatWasEntered()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("hunter2");
        using var source = new PromptedPassword(prompt);

        var password = source.Get(Db, attempt: 0);

        Assert.AreEqual("hunter2", Text(password));
        Assert.HasCount(1, prompt.Asks);
        Assert.AreEqual((Db, 0, Vault.MaxPasswordAttempts), prompt.Asks[0]);
    }

    [TestMethod]
    public void Get_SecondCallSameDatabase_ReusesTheHeldPasswordWithoutPrompting()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("hunter2");
        using var source = new PromptedPassword(prompt);
        source.Get(Db, 0);

        var again = source.Get(Db, 0);

        Assert.AreEqual("hunter2", Text(again));
        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void Get_DatabasePathComparison_IgnoresCase()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("hunter2");
        using var source = new PromptedPassword(prompt);
        source.Get(@"C:\Data\My.kdbx", 0);

        source.Get(@"c:\data\my.KDBX", 0);

        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void Get_ReturnsCopies_SoZeroingOneNeverWipesTheHeldPassword()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("hunter2");
        using var source = new PromptedPassword(prompt);

        var first = source.Get(Db, 0)!;
        Array.Clear(first);
        var second = source.Get(Db, 0)!;
        Array.Clear(second);
        var third = source.Get(Db, 0);

        Assert.AreEqual("hunter2", Text(third));
    }

    [TestMethod]
    public void Rejected_DropsTheHeldPassword_SoTheNextGetPromptsAgain()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("wrong");
        prompt.Enqueue("right");
        using var source = new PromptedPassword(prompt);
        source.Get(Db, 0);

        source.Rejected();
        var next = source.Get(Db, 1);

        Assert.AreEqual("right", Text(next));
        Assert.HasCount(2, prompt.Asks);
        Assert.AreEqual(1, prompt.Asks[1].Attempt);
    }

    [TestMethod]
    public void Rejected_ZeroesTheDroppedPassword()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("wrong");
        using var source = new PromptedPassword(prompt);
        source.Get(Db, 0);

        source.Rejected();

        CollectionAssert.AreEqual(new byte[prompt.Handed[0].Length], prompt.Handed[0]);
    }

    [TestMethod]
    public void Get_DifferentDatabase_PromptsAgainAndNeverReusesTheOtherOnesPassword()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("for-first");
        prompt.Enqueue("for-second");
        using var source = new PromptedPassword(prompt);
        source.Get(Db, 0);

        var other = source.Get(@"C:\data\other.kdbx", 0);

        Assert.AreEqual("for-second", Text(other));
        Assert.HasCount(2, prompt.Asks);
        CollectionAssert.AreEqual(new byte[prompt.Handed[0].Length], prompt.Handed[0]);
    }

    [TestMethod]
    public void Get_Cancelled_ReturnsNullAndHoldsNothing()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue(null);
        prompt.Enqueue("later");
        using var source = new PromptedPassword(prompt);

        Assert.IsNull(source.Get(Db, 0));
        var next = source.Get(Db, 0);

        Assert.AreEqual("later", Text(next));
        Assert.HasCount(2, prompt.Asks);
    }

    [TestMethod]
    public void Dispose_ZeroesTheHeldPassword()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("hunter2");
        var source = new PromptedPassword(prompt);
        source.Get(Db, 0);

        source.Dispose();

        CollectionAssert.AreEqual(new byte[prompt.Handed[0].Length], prompt.Handed[0]);
    }

    [TestMethod]
    public void Get_AfterDispose_Throws()
    {
        var source = new PromptedPassword(new FakePasswordPrompt());
        source.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => source.Get(Db, 0));
    }

    [TestMethod]
    public void Get_TwoCallersAtOnce_ShowOneDialogAndShareTheAnswer()
    {
        var prompt = new FakePasswordPrompt { Gate = new ManualResetEventSlim(initialState: false) };
        prompt.Enqueue("hunter2");
        using var source = new PromptedPassword(prompt);

        byte[]? GetPassword() => source.Get(Db, 0);

        var first = Task.Run(GetPassword);
        Assert.IsTrue(prompt.Asked.Wait(BoundedWait), "the first caller never reached the prompt.");
        var second = Task.Run(GetPassword);

        // The second caller must be waiting on the lock, not opening a second dialog.
        Thread.Sleep(300);
        prompt.Gate.Set();

        Assert.IsTrue(Task.WaitAll([first, second], BoundedWait), "the callers did not finish.");
        Assert.AreEqual("hunter2", Text(first.Result));
        Assert.AreEqual("hunter2", Text(second.Result));
        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void WithVault_RejectedHeldPassword_IsReplacedByAFreshPrompt()
    {
        // A held password that stops working (changed in KeePassXC) must re-prompt, not fail.
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("old");
        prompt.Enqueue("new");
        using var source = new PromptedPassword(prompt);
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: first");
        runner.Enqueue(1); // "old" now rejected
        runner.Enqueue(0, stdout: "k: second");
        var vault = new Vault(Db, new KeePassCli(runner, @"C:\Program Files\KeePassXC\keepassxc-cli.exe"), source);

        vault.Read("ntfy", ["k"]);
        var values = vault.Read("ntfy", ["k"]);

        Assert.AreEqual("second", Text(values["k"]));
        Assert.HasCount(2, prompt.Asks);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("old\n"), runner.Calls[1].Stdin);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("new\n"), runner.Calls[2].Stdin);
    }
}
