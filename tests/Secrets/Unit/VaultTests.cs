using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class VaultTests
{
    private const string Db = @"C:\data\my.kdbx";

    private static Vault CreateVault(FakeProcessRunner runner, FakePasswordSource passwords)
    {
        return new Vault(Db, new KeePassCli(runner, @"C:\Program Files\KeePassXC\keepassxc-cli.exe"), passwords);
    }

    [TestMethod]
    public void Read_SeveralKeys_UsesOneCliCall()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "a: one\r\nb: two\r\nc: three\r\n");
        var vault = CreateVault(runner, new FakePasswordSource("pw"));

        var values = vault.Read("ntfy", ["a", "c"]);

        Assert.HasCount(1, runner.Calls);
        Assert.HasCount(2, values);
        Assert.AreEqual("one", Encoding.UTF8.GetString(values["a"]));
        Assert.AreEqual("three", Encoding.UTF8.GetString(values["c"]));
    }

    [TestMethod]
    public void Read_WrongPasswordThenRight_RetriesAndReportsTheRejection()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(1);
        runner.Enqueue(0, stdout: "k: v");
        var passwords = new FakePasswordSource("wrong", "right");
        var vault = CreateVault(runner, passwords);

        var values = vault.Read("ntfy", ["k"]);

        Assert.AreEqual("v", Encoding.UTF8.GetString(values["k"]));
        CollectionAssert.AreEqual(new[] { 0, 1 }, passwords.Attempts);
        Assert.AreEqual(1, passwords.RejectedCount);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("right\n"), runner.Calls[1].Stdin);
    }

    [TestMethod]
    public void Read_PasswordRejectedEveryTime_ThrowsAfterThreeAttempts()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(1);
        runner.Enqueue(1);
        runner.Enqueue(1);
        var passwords = new FakePasswordSource("a", "b", "c");
        var vault = CreateVault(runner, passwords);

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["k"]));

        Assert.Contains("rejected 3 times", error.Message);
        Assert.HasCount(3, runner.Calls);
        Assert.AreEqual(3, passwords.RejectedCount);
    }

    [TestMethod]
    public void Read_NoPasswordAvailable_ThrowsWithoutRunningTheCli()
    {
        var runner = new FakeProcessRunner();
        var vault = CreateVault(runner, new FakePasswordSource());

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["k"]));

        Assert.Contains("no master password", error.Message);
        Assert.IsEmpty(runner.Calls);
    }

    [TestMethod]
    public void Read_CliFailure_ThrowsNamingEntryAndDatabaseAndDoesNotRetry()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(1, stderr: "Could not find entry with path nosuch.");
        var passwords = new FakePasswordSource("pw", "pw");
        var vault = CreateVault(runner, passwords);

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("nosuch", ["k"]));

        Assert.Contains("'nosuch'", error.Message);
        Assert.Contains(Db, error.Message);
        Assert.HasCount(1, runner.Calls);
        Assert.AreEqual(0, passwords.RejectedCount);
    }

    [TestMethod]
    public void Read_MissingKey_ThrowsNamingEntryAndKeyButNoValue()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "present: top-secret-value");
        var vault = CreateVault(runner, new FakePasswordSource("pw"));

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["present", "absent"]));

        Assert.Contains("'ntfy'", error.Message);
        Assert.Contains("'absent'", error.Message);
        Assert.DoesNotContain("top-secret-value", error.Message);
    }

    [TestMethod]
    public void Read_NoKeys_Throws()
    {
        var vault = CreateVault(new FakeProcessRunner(), new FakePasswordSource("pw"));

        Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", []));
    }

    [TestMethod]
    public void Read_DuplicateKey_Throws()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: v");
        var vault = CreateVault(runner, new FakePasswordSource("pw"));

        Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["k", "k"]));
    }

    [TestMethod]
    public void Read_ZeroesThePasswordItWasHanded()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: v");
        var passwords = new FakePasswordSource("hunter2");
        var vault = CreateVault(runner, passwords);

        vault.Read("ntfy", ["k"]);

        Assert.HasCount(1, passwords.Handed);
        CollectionAssert.AreEqual(new byte[passwords.Handed[0].Length], passwords.Handed[0]);
    }
}
