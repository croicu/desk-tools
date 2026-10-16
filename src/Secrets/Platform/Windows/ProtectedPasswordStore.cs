using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// Remembers the master password of one KeePass database across Service runs (issue #84), so the user is not asked
/// again after Service exits. The point of the design is that <b>no process the user can run unelevated can read it</b>
/// (the project's invariant: no secret value reaches medium integrity):
/// <list type="bullet">
/// <item>the password is encrypted with DPAPI at <i>machine</i> scope (<c>CryptProtectData</c> with
/// <c>CRYPTPROTECT_LOCAL_MACHINE</c>) -- not user scope, which any process of the same user can undo;</item>
/// <item>and the file sits in a directory only Administrators and SYSTEM can open (protected DACL, no inheritance),
/// created by the elevated Service. A medium-integrity process of the user can therefore neither read the blob nor
/// replace it. Only something already running as an administrator could.</item>
/// </list>
/// <b>The directory is not trusted just because it exists.</b> A user can create folders in <c>%ProgramData%</c>, so
/// before every read or write the directory's owner and DACL are checked: anything other than the trusted principals
/// (an owner could rewrite the DACL at will, so the owner counts), or a reparse point, means the store is not
/// used (a thrown <see cref="AppError"/>, which <see cref="PromptedPassword"/> turns into a warning and a normal
/// prompt). Consequently an <i>unelevated</i> Service (a dev run) cannot remember a password; it just prompts.
///
/// The blob holds the database path with the password (compared case-insensitively on load), so a password is only
/// ever offered for the database it was accepted for. Layout inside the DPAPI blob: magic <c>DTP1</c>, int32 path
/// byte length, path (UTF-8), int32 password length, password. Lives under <c>Platform/Windows/</c>; the Linux and
/// Neutral variants are placeholders.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedPasswordStore : IPasswordStore
{
    private const string Category = LogCategory.Secrets;
    private const string FileName = "password.bin";
    private const string DefaultDirName = "Desk Tools";

    private static readonly byte[] Magic = "DTP1"u8.ToArray();

    // Not a secret: DPAPI "optional entropy" that ties the blob to this purpose, so another DPAPI user of the same
    // machine scope cannot hand this store a blob made for something else.
    private static readonly byte[] Entropy = "Desk Tools KeePass master password v1"u8.ToArray();

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private readonly string _directory;
    private readonly SecurityIdentifier? _extraTrusted;

    /// <summary>
    /// <paramref name="directory"/> defaults to <c>%ProgramData%\Desk Tools</c>. <paramref name="extraTrusted"/> is a
    /// testing seam: one more principal the directory may belong to and grant access to (a test process is not an
    /// administrator and has to be able to read back what it wrote); null in production.
    /// </summary>
    internal ProtectedPasswordStore(string? directory = null, SecurityIdentifier? extraTrusted = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DefaultDirName);
        _extraTrusted = extraTrusted;
    }

    private string FilePath => Path.Combine(_directory, FileName);

    public byte[]? Load(string databasePath)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        EnsureTrustedDirectory(create: false);

        var blob = File.ReadAllBytes(FilePath);
        byte[] plain;
        try
        {
            plain = Unprotect(blob);
        }
        catch (AppError error)
        {
            // Not something this machine's DPAPI can open (tampered, damaged, from another machine): drop it so the
            // prompt's answer can replace it, rather than failing the same way on every run.
            Logger.Warning($"secrets: the remembered master password could not be decrypted ({error.Message}); discarding it.", Category);
            Forget();
            return null;
        }

        try
        {
            return ExtractPasswordFor(databasePath, plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(string databasePath, byte[] password)
    {
        EnsureTrustedDirectory(create: true);

        var plain = Pack(databasePath, password);
        byte[] blob;
        try
        {
            blob = Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Temp file in the same (protected) directory, then moved into place: a reader never sees a half-written file.
        var temp = Path.Combine(_directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(temp, blob);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Forget()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        // Deleting needs the same trust as reading: an untrusted directory is not ours to touch.
        EnsureTrustedDirectory(create: false);
        File.Delete(FilePath);
        Logger.Info("secrets: the remembered master password was deleted.", Category);
    }

    private static byte[] Pack(string databasePath, byte[] password)
    {
        var path = Encoding.UTF8.GetBytes(databasePath);
        var packed = new byte[Magic.Length + 4 + path.Length + 4 + password.Length];
        var offset = 0;

        Magic.CopyTo(packed, offset);
        offset += Magic.Length;
        BitConverter.TryWriteBytes(packed.AsSpan(offset), path.Length);
        offset += 4;
        path.CopyTo(packed, offset);
        offset += path.Length;
        BitConverter.TryWriteBytes(packed.AsSpan(offset), password.Length);
        offset += 4;
        password.CopyTo(packed, offset);
        return packed;
    }

    /// <summary>The password in <paramref name="plain"/> if it was remembered for <paramref name="databasePath"/>, else null; a malformed blob is an <see cref="AppError"/>.</summary>
    private byte[]? ExtractPasswordFor(string databasePath, byte[] plain)
    {
        var span = plain.AsSpan();
        if (span.Length < Magic.Length + 8 || !span[..Magic.Length].SequenceEqual(Magic))
        {
            throw new AppError("the remembered master password file has an unknown format.", Category);
        }

        var offset = Magic.Length;
        var pathLength = BitConverter.ToInt32(span.Slice(offset, 4));
        offset += 4;
        if (pathLength < 0 || offset + pathLength + 4 > span.Length)
        {
            throw new AppError("the remembered master password file is damaged.", Category);
        }

        var rememberedPath = Encoding.UTF8.GetString(span.Slice(offset, pathLength));
        offset += pathLength;
        var passwordLength = BitConverter.ToInt32(span.Slice(offset, 4));
        offset += 4;
        if (passwordLength < 0 || offset + passwordLength != span.Length)
        {
            throw new AppError("the remembered master password file is damaged.", Category);
        }

        if (!string.Equals(rememberedPath, databasePath, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Info($"secrets: the remembered master password is for another database ('{rememberedPath}'); not using it for '{databasePath}'.", Category);
            return null;
        }

        var password = GC.AllocateArray<byte>(passwordLength, pinned: true);
        span.Slice(offset, passwordLength).CopyTo(password);
        return password;
    }

    /// <summary>
    /// Throws <see cref="AppError"/> unless the directory exists (created, owned by Administrators with only
    /// Administrators/SYSTEM allowed in, when <paramref name="create"/>) and is trustworthy.
    /// </summary>
    private void EnsureTrustedDirectory(bool create)
    {
        var info = new DirectoryInfo(_directory);
        if (!info.Exists)
        {
            if (!create)
            {
                throw new AppError($"the master password directory '{_directory}' does not exist.", Category);
            }

            CreateProtectedDirectory(info);
            info.Refresh();
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppError($"'{_directory}' is a reparse point (a link); refusing to keep the master password there.", Category);
        }

        var security = info.GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner))
        {
            throw new AppError($"'{_directory}' is owned by '{owner?.Value ?? "(unknown)"}', not by Administrators; refusing to keep the master password there.", Category);
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !IsTrusted(sid))
            {
                throw new AppError($"'{_directory}' grants access to '{sid.Value}'; only Administrators and SYSTEM may. Refusing to keep the master password there.", Category);
            }
        }
    }

    private void CreateProtectedDirectory(DirectoryInfo info)
    {
        var security = new DirectorySecurity();

        // No inheritance from %ProgramData% (which lets Users in); explicit rules only.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, LocalSystem);
        AddFullControl(security, Administrators);
        if (_extraTrusted is not null)
        {
            AddFullControl(security, _extraTrusted);
        }

        info.Create(security);
        Logger.Info($"secrets: created '{_directory}', accessible to Administrators and SYSTEM only.", Category);
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private bool IsTrusted(SecurityIdentifier sid)
    {
        return sid.Equals(LocalSystem) || sid.Equals(Administrators) || sid.Equals(CreatorOwner) || (_extraTrusted is not null && sid.Equals(_extraTrusted));
    }

    private static byte[] Protect(byte[] plain)
    {
        return Crypt(plain, protect: true);
    }

    private static byte[] Unprotect(byte[] blob)
    {
        return Crypt(blob, protect: false);
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new NativeMethods.DataBlob();
        var entropyBlob = new NativeMethods.DataBlob();
        var outBlob = new NativeMethods.DataBlob();

        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.Length = input.Length;
            inBlob.Data = inHandle.AddrOfPinnedObject();
            entropyBlob.Length = Entropy.Length;
            entropyBlob.Data = entropyHandle.AddrOfPinnedObject();

            const uint uiForbidden = 0x1;
            const uint localMachine = 0x4;

            var ok = protect
                ? NativeMethods.CryptProtectData(ref inBlob, "Desk Tools", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden | localMachine, out outBlob)
                : NativeMethods.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, uiForbidden, out outBlob);
            if (!ok)
            {
                throw new AppError($"DPAPI {(protect ? "CryptProtectData" : "CryptUnprotectData")} failed (Win32 error {Marshal.GetLastWin32Error()}).", Category);
            }

            var result = new byte[outBlob.Length];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Length);
            return result;
        }
        finally
        {
            if (outBlob.Data != IntPtr.Zero)
            {
                // The output buffer is DPAPI's own allocation, holding plaintext when unprotecting: wipe it before freeing.
                if (!protect)
                {
                    Marshal.Copy(new byte[outBlob.Length], 0, outBlob.Data, outBlob.Length);
                }

                NativeMethods.LocalFree(outBlob.Data);
            }

            inHandle.Free();
            entropyHandle.Free();
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
