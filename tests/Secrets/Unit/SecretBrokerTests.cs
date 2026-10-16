using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// <see cref="SecretBroker"/> composes the pieces the other tests cover one by one (database location,
/// CLI, vault, password source); these check the composition: lazy, serialized, values decoded, buffers
/// zeroed, and failures surfaced as <see cref="AppError"/> so the host can degrade.
/// </summary>
[TestClass]
public sealed class SecretBrokerTests
{
    private const string CliPath = @"C:\Program Files\KeePassXC\keepassxc-cli.exe";

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private static string NewKdbx()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"secret-broker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Secrets.kdbx");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static SecretBroker Create(FakeProcessRunner runner, FakePasswordSource passwords, FakeDatabasePathStore store, Func<string>? cliPath = null)
    {
        return new SecretBroker(new DatabaseLocation(store, new FakeFilePicker(null)), cliPath ?? (() => CliPath), runner, passwords);
    }

    [TestMethod]
    public void Read_ReturnsTheValuesAsText()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "desk-pager: abc-123\nother: x");
        using var broker = Create(runner, new FakePasswordSource("password"), new FakeDatabasePathStore(NewKdbx()));

        var values = broker.Read("ntfy", ["desk-pager", "other"]);

        Assert.AreEqual("abc-123", values["desk-pager"]);
        Assert.AreEqual("x", values["other"]);
        Assert.AreEqual(CliPath, runner.Calls[0].FileName);
    }

    [TestMethod]
    public void Read_ZeroesThePasswordItWasHanded()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: v");
        var passwords = new FakePasswordSource("password");
        using var broker = Create(runner, passwords, new FakeDatabasePathStore(NewKdbx()));

        broker.Read("ntfy", ["k"]);

        CollectionAssert.AreEqual(new byte[passwords.Handed[0].Length], passwords.Handed[0]);
    }

    [TestMethod]
    public void Read_MissingKey_ThrowsAppErrorNamingTheEntry()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "other: v");
        using var broker = Create(runner, new FakePasswordSource("password"), new FakeDatabasePathStore(NewKdbx()));

        var error = Assert.ThrowsExactly<AppError>(() => broker.Read("ntfy", ["desk-pager"]));

        StringAssert.Contains(error.Message, "ntfy");
    }

    [TestMethod]
    public void Read_DatabaseUnusable_ThrowsAppError()
    {
        var runner = new FakeProcessRunner();
        using var broker = Create(runner, new FakePasswordSource("password"), new FakeDatabasePathStore(@"relative\Secrets.kdbx"));

        Assert.ThrowsExactly<AppError>(() => broker.Read("ntfy", ["k"]));
        Assert.IsEmpty(runner.Calls);
    }

    [TestMethod]
    public void Read_CliNotInstalled_ThrowsAppError()
    {
        var runner = new FakeProcessRunner();

        string MissingCli() => throw new AppError("keepassxc-cli not found.");

        using var broker = Create(runner, new FakePasswordSource("password"), new FakeDatabasePathStore(NewKdbx()), MissingCli);

        Assert.ThrowsExactly<AppError>(() => broker.Read("ntfy", ["k"]));
    }

    [TestMethod]
    public void Construction_DoesNothingUntilTheFirstRead()
    {
        var runner = new FakeProcessRunner();
        var passwords = new FakePasswordSource("password");
        var cliLookups = 0;

        string CountingCli()
        {
            cliLookups++;
            return CliPath;
        }

        using var broker = Create(runner, passwords, new FakeDatabasePathStore(null), CountingCli);

        Assert.AreEqual(0, cliLookups);
        Assert.IsEmpty(runner.Calls);
        Assert.IsEmpty(passwords.Attempts);
    }

    [TestMethod]
    public void Read_SecondCall_UsesTheSavedDatabaseWithoutAskingAgain()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(0, stdout: "k: one");
        runner.Enqueue(0, stdout: "k: two");
        var picker = new FakeFilePicker(null);
        var store = new FakeDatabasePathStore(NewKdbx());
        using var broker = new SecretBroker(new DatabaseLocation(store, picker), () => CliPath, runner, new FakePasswordSource("password", "password"));

        broker.Read("a", ["k"]);
        var second = broker.Read("b", ["k"]);

        Assert.AreEqual("two", second["k"]);
        Assert.IsEmpty(picker.Asks);
    }

    [TestMethod]
    public void Dispose_DisposesAnOwnedPasswordSource()
    {
        var passwords = new DisposablePasswordSource();
        var broker = new SecretBroker(new DatabaseLocation(new FakeDatabasePathStore(null), new FakeFilePicker(null)), () => CliPath, new FakeProcessRunner(), passwords);

        broker.Dispose();

        Assert.IsTrue(passwords.Disposed);
    }

    private sealed class DisposablePasswordSource : IPasswordSource, IDisposable
    {
        public bool Disposed { get; private set; }

        public byte[]? Get(string databasePath, int attempt) => Encoding.UTF8.GetBytes("password");

        public void Rejected()
        {
        }

        public void Accepted()
        {
        }

        public void Forget()
        {
        }

        public void Dispose() => Disposed = true;
    }
}
