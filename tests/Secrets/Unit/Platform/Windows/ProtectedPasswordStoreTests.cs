using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Croicu.Desk.Tools.Base;
using Croicu.Desk.Tools.Base.Sinks;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// The real <see cref="ProtectedPasswordStore"/>: real DPAPI and real ACLs, in a throwaway directory per test (never
/// <c>%ProgramData%</c>). A test process is not an administrator, so the store is told to also trust the current user
/// (<c>extraTrusted</c>); everything else -- the admin-only DACL, refusing a directory that lets anyone else in --
/// is exactly what production does. Only compiled in for win-x64.
/// </summary>
[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ProtectedPasswordStoreTests
{
    private const string Db = @"C:\data\my.kdbx";

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private readonly List<string> _directories = new();

    [TestCleanup]
    public void Cleanup()
    {
        Logger.SetLogger(null);
        foreach (var directory in _directories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is harmless.
            }
        }
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"password-store-tests-{Guid.NewGuid():N}");
        _directories.Add(directory);
        return directory;
    }

    private ProtectedPasswordStore NewStore(string? directory = null)
    {
        return new ProtectedPasswordStore(directory ?? NewDirectory(), extraTrusted: Me);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class RecordingSink : DiagnosticsLog
    {
        public List<string> Messages { get; } = new();

        public int Warnings { get; private set; }

        public override TelemetryRecord Log(TelemetryLevel level, string message, string category = DiagnosticsCategories.General)
        {
            Messages.Add(message);
            if (level == TelemetryLevel.Warning)
            {
                Warnings++;
            }

            return base.Log(level, message, category);
        }
    }

    [TestMethod]
    public void SaveThenLoad_ReturnsTheSamePassword_ForTheSameDatabase()
    {
        var store = NewStore();

        store.Save(Db, Bytes("p@ss w0rd-é✓"));
        var loaded = store.Load(Db);

        CollectionAssert.AreEqual(Bytes("p@ss w0rd-é✓"), loaded);
    }

    [TestMethod]
    public void Load_ComparesTheDatabasePathCaseInsensitively()
    {
        var store = NewStore();
        store.Save(@"C:\Data\My.kdbx", Bytes("pw"));

        Assert.IsNotNull(store.Load(@"c:\data\my.KDBX"));
    }

    [TestMethod]
    public void Load_ForAnotherDatabase_ReturnsNull_AndKeepsWhatIsRemembered()
    {
        var store = NewStore();
        store.Save(Db, Bytes("pw"));

        Assert.IsNull(store.Load(@"C:\data\other.kdbx"));
        Assert.IsNotNull(store.Load(Db), "asking about another database must not throw away the remembered one.");
    }

    [TestMethod]
    public void Load_NothingSaved_ReturnsNull()
    {
        Assert.IsNull(NewStore().Load(Db));
    }

    [TestMethod]
    public void Save_ReplacesWhatWasRemembered()
    {
        var store = NewStore();
        store.Save(Db, Bytes("first"));

        store.Save(Db, Bytes("second"));

        CollectionAssert.AreEqual(Bytes("second"), store.Load(Db));
    }

    [TestMethod]
    public void Forget_DeletesIt_AndForgettingNothingIsNotAnError()
    {
        var store = NewStore();
        store.Save(Db, Bytes("pw"));

        store.Forget();
        store.Forget();

        Assert.IsNull(store.Load(Db));
    }

    [TestMethod]
    public void TheFileOnDisk_ContainsNeitherThePasswordNorThePathInTheClear()
    {
        var directory = NewDirectory();
        var store = NewStore(directory);

        store.Save(Db, Bytes("unmistakable-pass-phrase"));

        var onDisk = File.ReadAllBytes(Path.Combine(directory, "password.bin"));
        var asText = Encoding.UTF8.GetString(onDisk) + Encoding.Unicode.GetString(onDisk);
        Assert.DoesNotContain("unmistakable-pass-phrase", asText);
        Assert.DoesNotContain("my.kdbx", asText);
    }

    [TestMethod]
    public void Load_ADamagedFile_ReturnsNull_WarnsAndDiscardsIt()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var directory = NewDirectory();
        var store = NewStore(directory);
        store.Save(Db, Bytes("pw"));
        var file = Path.Combine(directory, "password.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        var loaded = store.Load(Db);

        Assert.IsNull(loaded);
        Assert.IsGreaterThanOrEqualTo(1, sink.Warnings);
        Assert.IsFalse(File.Exists(file), "a file that cannot be decrypted is discarded so the next answer can replace it.");
    }

    [TestMethod]
    public void Save_CreatesADirectoryNobodyButAdministratorsAndSystemCanEnter()
    {
        var directory = NewDirectory();

        NewStore(directory).Save(Db, Bytes("pw"));

        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, "it must not inherit %ProgramData%'s permissions (which let Users in).");

        var allowed = new List<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                allowed.Add((SecurityIdentifier)rule.IdentityReference);
            }
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Assert.Contains(administrators, allowed);
        Assert.Contains(localSystem, allowed);
        Assert.DoesNotContain(everyone, allowed);
        Assert.DoesNotContain(users, allowed);
    }

    [TestMethod]
    public void ADirectoryThatLetsOtherPeopleIn_IsNotTrusted_NothingIsReadOrWritten()
    {
        // The situation a user can create themselves: %ProgramData% lets them make folders, and a folder they made is
        // theirs to loosen. The store must refuse to keep (or trust) a password there.
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var security = new DirectoryInfo(directory).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute | FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        var store = NewStore(directory);

        var save = Assert.ThrowsExactly<AppError>(() => store.Save(Db, Bytes("pw")));

        Assert.Contains("only Administrators and SYSTEM may", save.Message);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "password.bin")));
    }

    [TestMethod]
    public void AStoreThatDoesNotTrustTheCurrentUser_RefusesADirectoryOwnedByThem()
    {
        // Production has no "extra trusted" principal: a non-administrator who created the directory owns it, and an
        // owner can rewrite the DACL at will -- so it is not trusted, however restrictive its ACL looks today.
        var directory = NewDirectory();
        NewStore(directory).Save(Db, Bytes("pw"));
        var production = new ProtectedPasswordStore(directory);

        var error = Assert.ThrowsExactly<AppError>(() => production.Load(Db));

        Assert.IsTrue(error.Message.Contains("owned by") || error.Message.Contains("grants access"), error.Message);
    }

    [TestMethod]
    public void NeverLogsThePassword()
    {
        var sink = new RecordingSink();
        Logger.SetLogger(sink);
        var store = NewStore();

        store.Save(Db, Bytes("unmistakable-pass-phrase"));
        store.Load(Db);
        store.Load(@"C:\data\other.kdbx");
        store.Forget();

        Assert.IsNotEmpty(sink.Messages);
        Assert.DoesNotContain("unmistakable-pass-phrase", string.Join(" | ", sink.Messages));
    }
}
