using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Integration;

/// <summary>
/// Runs the real <c>keepassxc-cli</c> against the dummy database (master password "password", one
/// entry "ntfy" with a <c>desk-pager: &lt;guid&gt;</c> Notes line). Excluded from the default
/// <c>dotnet test</c> (<c>TestCategory!=Integration</c>) because it needs KeePassXC installed
/// machine-wide; reports Inconclusive, not a failure, where it isn't.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class KeePassTests
{
    private static string DatabasePath => Path.Combine(AppContext.BaseDirectory, "data", "Secrets.kdbx");

    private static Vault CreateVault(params string[] passwords)
    {
        string exe;
        try
        {
            exe = CliLocation.Resolve();
        }
        catch (AppError error)
        {
            Assert.Inconclusive(error.Message);
            throw;
        }

        return new Vault(DatabasePath, new KeePassCli(new ProcessRunner(), exe), new Unit.FakePasswordSource(passwords));
    }

    [TestMethod]
    public void Read_DummyDatabase_ReturnsTheGuid()
    {
        var vault = CreateVault("password");

        var values = vault.Read("ntfy", ["desk-pager"]);

        var text = Encoding.UTF8.GetString(values["desk-pager"]);
        Assert.IsTrue(Guid.TryParse(text, out _), "expected the dummy value to be a GUID.");
    }

    [TestMethod]
    public void Read_WrongPasswordThenRight_Succeeds()
    {
        var vault = CreateVault("wrong", "password");

        var values = vault.Read("ntfy", ["desk-pager"]);

        Assert.HasCount(1, values);
    }

    [TestMethod]
    public void Read_WrongPasswordEveryTime_ThrowsAfterThreeAttempts()
    {
        var vault = CreateVault("a", "b", "c");

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["desk-pager"]));

        Assert.Contains("rejected 3 times", error.Message);
    }

    [TestMethod]
    public void Read_MissingEntry_ThrowsNamingTheEntry()
    {
        var vault = CreateVault("password");

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("no-such-entry", ["desk-pager"]));

        Assert.Contains("'no-such-entry'", error.Message);
    }

    [TestMethod]
    public void Read_MissingKey_ThrowsNamingEntryAndKey()
    {
        var vault = CreateVault("password");

        var error = Assert.ThrowsExactly<AppError>(() => vault.Read("ntfy", ["no-such-key"]));

        Assert.Contains("'ntfy'", error.Message);
        Assert.Contains("'no-such-key'", error.Message);
    }
}
