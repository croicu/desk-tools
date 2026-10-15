using System.Text;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class PromptedPasswordTests
{
    private const string Db = @"C:\data\my.kdbx";
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    // --- answers that arrive at once (queued in the fake) ---

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

    // --- the person takes their time (the dialog is left open and answered by hand) ---

    /// <summary>
    /// Runs a launch that blocks in <see cref="PromptedPassword.Get"/> on its own thread, not a thread-pool
    /// one: these tests block pool threads of their own while they wait, so under load (the whole solution
    /// testing at once) a pooled launch could sit queued behind them past any timeout.
    /// </summary>
    private static Task<byte[]?> StartLaunch(Func<byte[]?> launch)
    {
        return Task.Factory.StartNew(launch, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static (Task<byte[]?> Waiting, FakePasswordPrompt Prompt, PromptedPassword Source) StartWaiting()
    {
        var prompt = new FakePasswordPrompt();
        var source = new PromptedPassword(prompt);

        byte[]? GetPassword() => source.Get(Db, 0);

        var waiting = StartLaunch(GetPassword);
        Assert.IsTrue(prompt.Asked.Wait(BoundedWait), $"the dialog never opened (the launch is {waiting.Status}: {waiting.Exception?.GetBaseException().Message}).");
        return (waiting, prompt, source);
    }

    [TestMethod]
    public void Get_DialogNotAnsweredYet_WaitsForTheUserAsLongAsItTakes()
    {
        var (waiting, prompt, source) = StartWaiting();
        using (source)
        {
            // Long past any timer a previous design had: it must still be waiting, not have given up.
            Thread.Sleep(700);
            Assert.IsFalse(waiting.IsCompleted, "the launch must wait for the answer, not time out.");
            Assert.IsFalse(prompt.WasCancelled);

            prompt.Answer("hunter2");

            Assert.IsTrue(waiting.Wait(BoundedWait));
            Assert.AreEqual("hunter2", Text(waiting.Result));
            Assert.HasCount(1, prompt.Asks);
        }
    }

    [TestMethod]
    public void Get_TwoLaunchesAtOnce_ShowOneDialogAndShareTheAnswer()
    {
        var (first, prompt, source) = StartWaiting();
        using (source)
        {
            byte[]? GetPassword() => source.Get(Db, 0);
            var second = StartLaunch(GetPassword);

            // The second launch joins the open dialog; it must not open another one.
            Thread.Sleep(300);
            Assert.HasCount(1, prompt.Asks);

            prompt.Answer("hunter2");

            Assert.IsTrue(Task.WaitAll([first, second], BoundedWait), "the launches did not finish.");
            Assert.AreEqual("hunter2", Text(first.Result));
            Assert.AreEqual("hunter2", Text(second.Result));
            Assert.HasCount(1, prompt.Asks);
        }
    }

    [TestMethod]
    public void Get_DialogCancelledByTheUser_ReleasesTheWaiterWithNull()
    {
        var (waiting, prompt, source) = StartWaiting();
        using (source)
        {
            prompt.Answer(null);

            Assert.IsTrue(waiting.Wait(BoundedWait));
            Assert.IsNull(waiting.Result);
        }
    }

    [TestMethod]
    public void Dispose_WhileAWaiterIsWaiting_ClosesTheDialogAndReleasesIt()
    {
        var (waiting, prompt, source) = StartWaiting();

        source.Dispose();

        Assert.IsTrue(waiting.Wait(BoundedWait), "Service shutting down must not leave a launch blocked forever.");
        Assert.IsNull(waiting.Result);
        Assert.IsTrue(prompt.WasCancelled, "the open dialog must be told to close.");
    }

    [TestMethod]
    public void Get_RequestForAnotherDatabaseWhileADialogIsOpen_ReturnsNullWithoutASecondDialog()
    {
        var (waiting, prompt, source) = StartWaiting();
        using (source)
        {
            var other = source.Get(@"C:\data\other.kdbx", 0);

            Assert.IsNull(other);
            Assert.HasCount(1, prompt.Asks);

            prompt.Answer("hunter2");
            Assert.IsTrue(waiting.Wait(BoundedWait));
        }
    }

    [TestMethod]
    public void Get_DialogCouldNotBeShown_ReturnsNullInsteadOfThrowing()
    {
        var (waiting, prompt, source) = StartWaiting();
        using (source)
        {
            prompt.Fail(new InvalidOperationException("no desktop"));

            Assert.IsTrue(waiting.Wait(BoundedWait));
            Assert.IsNull(waiting.Result);
        }
    }

    [TestMethod]
    public void Get_AfterACancelledDialog_AsksAgain()
    {
        var (waiting, prompt, source) = StartWaiting();
        using (source)
        {
            prompt.Answer(null);
            Assert.IsTrue(waiting.Wait(BoundedWait));

            // The cancelled dialog leaves the pending slot asynchronously, so a Get right at that
            // moment can still see it (and return null once); the next one opens a new dialog.
            prompt.Enqueue("second-try");
            byte[]? next = null;
            var deadline = DateTime.UtcNow + BoundedWait;
            while (next is null && DateTime.UtcNow < deadline)
            {
                next = source.Get(Db, 1);
                if (next is null)
                {
                    Thread.Sleep(20);
                }
            }

            Assert.AreEqual("second-try", Text(next));
            Assert.HasCount(2, prompt.Asks);
            Assert.AreEqual(1, prompt.Asks[1].Attempt);
        }
    }

    // --- together with the vault ---

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
