using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Remembering the master password across Service runs ([issue #84](https://github.com/croicu/desk-tools/issues/84)):
/// <see cref="PromptedPassword"/>'s policy around an <see cref="IPasswordStore"/>. Only a password KeePassXC
/// actually accepted is saved; a remembered one is loaded instead of asking; a rejected one is forgotten; and a store
/// that fails is a warning, never a failed launch. The store here is an in-memory fake -- nothing touches the disk.
/// </summary>
[TestClass]
public sealed class PromptedPasswordRememberTests
{
    private const string Db = @"C:\data\my.kdbx";

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private sealed class RecordingSink : DiagnosticsLog
    {
        public List<TelemetryRecord> Received { get; } = new();

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            var record = base.Log(level, message, category);
            Received.Add(record);
            return record;
        }

        public int Warnings()
        {
            var count = 0;
            foreach (var record in Received)
            {
                if (record.Level == TelemetryLevel.Warning)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    [TestMethod]
    public void Get_PasswordRememberedForThisDatabase_IsUsedWithoutAskingAnyone()
    {
        var prompt = new FakePasswordPrompt();
        var store = new FakePasswordStore();
        store.Seed(Db, "remembered");
        using var source = new PromptedPassword(prompt, store);

        var password = source.Get(Db, attempt: 0);

        Assert.AreEqual("remembered", Text(password));
        Assert.IsEmpty(prompt.Asks, "a remembered password must not open the dialog.");
    }

    [TestMethod]
    public void Get_RememberedPasswordIsCaseInsensitiveOnThePath()
    {
        var store = new FakePasswordStore();
        store.Seed(@"C:\Data\My.kdbx", "remembered");
        using var source = new PromptedPassword(new FakePasswordPrompt(), store);

        Assert.AreEqual("remembered", Text(source.Get(@"c:\data\my.KDBX", 0)));
    }

    [TestMethod]
    public void Get_PasswordRememberedForAnotherDatabase_IsNotUsed_AndTheUserIsAsked()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore();
        store.Seed(@"C:\data\other.kdbx", "not-this-one");
        using var source = new PromptedPassword(prompt, store);

        var password = source.Get(Db, 0);

        Assert.AreEqual("typed", Text(password));
        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void Get_RememberedPassword_IsLoadedOnceAndThenHeld()
    {
        var store = new FakePasswordStore();
        store.Seed(Db, "remembered");
        using var source = new PromptedPassword(new FakePasswordPrompt(), store);

        source.Get(Db, 0);
        source.Get(Db, 0);

        Assert.AreEqual(1, store.Loads);
    }

    [TestMethod]
    public void Get_SecondAttemptDoesNotLoadTheRememberedPassword()
    {
        // After a failed attempt the remembered password is gone (Rejected forgets it); asking the store again for
        // attempt 1 would only hand back the same bad one if forgetting had failed.
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore();
        store.Seed(Db, "stale");
        using var source = new PromptedPassword(prompt, store);

        var password = source.Get(Db, attempt: 1);

        Assert.AreEqual("typed", Text(password));
        Assert.AreEqual(0, store.Loads);
    }

    [TestMethod]
    public void Accepted_AfterTheUserTypedIt_RemembersItOnce()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore();
        using var source = new PromptedPassword(prompt, store);
        source.Get(Db, 0);

        source.Accepted();
        source.Accepted();

        Assert.AreEqual(1, store.Saves);
        Assert.AreEqual(Db, store.RememberedFor);
        Assert.AreEqual("typed", Text(store.Remembered));
    }

    [TestMethod]
    public void Accepted_BeforeKeePassConfirmedNothing_SavesNothing()
    {
        // Only Accepted() (called after a successful read) may write: a password that was merely typed is not yet known to be right.
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore();
        using var source = new PromptedPassword(prompt, store);

        source.Get(Db, 0);

        Assert.AreEqual(0, store.Saves);
    }

    [TestMethod]
    public void Accepted_ForAPasswordThatCameFromTheStore_DoesNotWriteItBack()
    {
        var store = new FakePasswordStore();
        store.Seed(Db, "remembered");
        using var source = new PromptedPassword(new FakePasswordPrompt(), store);
        source.Get(Db, 0);

        source.Accepted();

        Assert.AreEqual(0, store.Saves);
    }

    [TestMethod]
    public void Rejected_ForgetsTheRememberedPassword_AndTheNextAttemptAsks()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("new-password");
        var store = new FakePasswordStore();
        store.Seed(Db, "changed-in-keepass");
        using var source = new PromptedPassword(prompt, store);
        source.Get(Db, 0);

        source.Rejected();
        var next = source.Get(Db, 1);

        Assert.AreEqual(1, store.Forgets);
        Assert.IsNull(store.Remembered);
        Assert.AreEqual("new-password", Text(next));
        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void Forget_DropsTheHeldPasswordAndTheRememberedOne_SoTheNextGetAsks()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed-again");
        var store = new FakePasswordStore();
        store.Seed(Db, "remembered");
        using var source = new PromptedPassword(prompt, store);
        source.Get(Db, 0);

        source.Forget();
        var next = source.Get(Db, 0);

        Assert.AreEqual(1, store.Forgets);
        Assert.AreEqual("typed-again", Text(next));
        Assert.HasCount(1, prompt.Asks);
    }

    [TestMethod]
    public void Dispose_DoesNotForgetTheRememberedPassword()
    {
        // Service exiting is exactly the moment the remembered password is for.
        var store = new FakePasswordStore();
        store.Seed(Db, "remembered");
        var source = new PromptedPassword(new FakePasswordPrompt(), store);
        source.Get(Db, 0);

        source.Dispose();

        Assert.AreEqual(0, store.Forgets);
        Assert.IsNotNull(store.Remembered);
    }

    [TestMethod]
    public void NoStore_MeansTheFeatureIsOff_AndNothingThrows()
    {
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        using var source = new PromptedPassword(prompt, store: null);

        Assert.AreEqual("typed", Text(source.Get(Db, 0)));
        source.Accepted();
        source.Rejected();
        source.Forget();
    }

    [TestMethod]
    public void Get_TheStoreCannotBeRead_WarnsAndAsksAsIfNothingWereRemembered()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore { LoadFails = new AppError("directory not trusted") };
        using var source = new PromptedPassword(prompt, store);

        var password = source.Get(Db, 0);

        Assert.AreEqual("typed", Text(password));
        Assert.IsGreaterThanOrEqualTo(1, sink.Warnings());
    }

    [TestMethod]
    public void Accepted_TheStoreCannotBeWritten_WarnsAndTheLaunchGoesOn()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue("typed");
        var store = new FakePasswordStore { SaveFails = new AppError("directory not trusted") };
        using var source = new PromptedPassword(prompt, store);
        source.Get(Db, 0);

        source.Accepted();

        Assert.IsGreaterThanOrEqualTo(1, sink.Warnings());
        Assert.AreEqual("typed", Text(source.Get(Db, 0)), "the password stays held for this run even though it could not be remembered.");
    }

    [TestMethod]
    public void Rejected_AndForget_TheStoreCannotDelete_NeverThrow()
    {
        var store = new FakePasswordStore { ForgetFails = new AppError("directory not trusted") };
        store.Seed(Db, "remembered");
        using var source = new PromptedPassword(new FakePasswordPrompt(), store);
        source.Get(Db, 0);

        source.Rejected();
        source.Forget();
    }

    [TestMethod]
    public void NeverLogsThePassword()
    {
        const string secret = "s3cr3t-master-pw";
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var prompt = new FakePasswordPrompt();
        prompt.Enqueue(secret);
        var store = new FakePasswordStore();
        using var source = new PromptedPassword(prompt, store);
        source.Get(Db, 0);
        source.Accepted();
        source.Forget();
        store.Seed(Db, secret);
        source.Get(Db, 0);
        source.Rejected();

        var all = new List<string>();
        foreach (var record in sink.Received)
        {
            all.Add(record.Message);
        }

        Assert.DoesNotContain(secret, string.Join("\n", all));
    }
}
