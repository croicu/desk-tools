using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

[TestClass]
public sealed class DatabaseLocationTests
{
    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private static string NewKdbx(string name = "Secrets.kdbx")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"db-location-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [TestMethod]
    public void Resolve_SavedPathUsable_ReturnsItAndNeverOpensTheDialog()
    {
        var saved = NewKdbx();
        var store = new FakeDatabasePathStore(saved);
        var picker = new FakeFilePicker(null);

        var path = new DatabaseLocation(store, picker).Resolve();

        Assert.AreEqual(saved, path);
        Assert.IsEmpty(picker.Asks, "the picker must only open when the setting is missing or empty.");
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Resolve_SettingMissingOrEmpty_AsksOnceAndSavesTheAnswer(string? saved)
    {
        var chosen = NewKdbx();
        var store = new FakeDatabasePathStore(saved);
        var picker = new FakeFilePicker(chosen);

        var path = new DatabaseLocation(store, picker).Resolve();

        Assert.AreEqual(chosen, path);
        Assert.AreEqual(chosen, store.Saved);
        Assert.AreEqual(1, store.Writes);
        Assert.HasCount(1, picker.Asks);
        Assert.AreEqual((DatabaseLocation.PickerTitle, "KeePass databases", "kdbx"), picker.Asks[0]);
    }

    [TestMethod]
    public void Resolve_UserCancelsTheDialog_ThrowsADetailedErrorAndSavesNothing()
    {
        var store = new FakeDatabasePathStore(null);

        var error = Assert.ThrowsExactly<AppError>(() => new DatabaseLocation(store, new FakeFilePicker(null)).Resolve());

        Assert.Contains("none was chosen", error.Message);
        Assert.Contains("'secrets.databasePath' in the fake store", error.Message, "the error must say where the setting lives.");
        Assert.Contains(".kdbx", error.Message);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public void Resolve_PickedFileIsNotAKdbx_ThrowsAndSavesNothing()
    {
        var store = new FakeDatabasePathStore(null);
        var picker = new FakeFilePicker(NewKdbx("notes.txt"));

        var error = Assert.ThrowsExactly<AppError>(() => new DatabaseLocation(store, picker).Resolve());

        Assert.Contains("cannot be used", error.Message);
        Assert.Contains("Nothing was saved", error.Message);
        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public void Resolve_PickedPathIsUnc_ThrowsAndSavesNothing()
    {
        var store = new FakeDatabasePathStore(null);
        var picker = new FakeFilePicker(@"\\attacker\share\Secrets.kdbx");

        Assert.ThrowsExactly<AppError>(() => new DatabaseLocation(store, picker).Resolve());

        Assert.AreEqual(0, store.Writes);
    }

    [TestMethod]
    public void Resolve_SavedPathIsUnc_ThrowsWithoutOpeningTheDialogOrChangingAnything()
    {
        // The per-user file is writable by any process running as the user: a UNC path planted there
        // must neither be opened by the elevated Service nor silently replaced by a dialog.
        var store = new FakeDatabasePathStore(@"\\attacker\share\Secrets.kdbx");
        var picker = new FakeFilePicker(NewKdbx());

        var error = Assert.ThrowsExactly<AppError>(() => new DatabaseLocation(store, picker).Resolve());

        Assert.Contains("cannot be used", error.Message);
        Assert.Contains("UNC", error.Message);
        Assert.IsEmpty(picker.Asks);
        Assert.AreEqual(0, store.Writes);
        Assert.AreEqual(@"\\attacker\share\Secrets.kdbx", store.Saved);
    }

    [TestMethod]
    public void Resolve_SavedPathNoLongerExists_ThrowsInsteadOfReopeningTheDialog()
    {
        var gone = NewKdbx();
        File.Delete(gone);
        var store = new FakeDatabasePathStore(gone);
        var picker = new FakeFilePicker(NewKdbx());

        var error = Assert.ThrowsExactly<AppError>(() => new DatabaseLocation(store, picker).Resolve());

        Assert.Contains("no such file", error.Message);
        Assert.Contains("Fix or delete that setting", error.Message);
        Assert.IsEmpty(picker.Asks);
    }
}
