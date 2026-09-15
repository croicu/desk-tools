namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Cross-process single-instance guard for <see cref="Host"/>, backed by a named <see cref="Mutex"/>.
/// A named <see cref="Mutex"/> itself (<see cref="Mutex.WaitOne()"/>, a timeout,
/// <see cref="AbandonedMutexException"/>) is genuinely cross-platform in .NET -- confirmed against
/// the BCL docs, not assumed: "Named system mutexes are visible throughout the operating system...
/// On Unix-like operating systems, the file system is used in the implementation of named mutexes."
/// What's actually Windows-specific is narrower: the "Local\"/"Global\" prefix's *special meaning*
/// is a Terminal-Services session-scoping concept with no Unix equivalent, and the same docs
/// separately warn "the backslash (\) is a reserved character... don't use [one]... except as
/// specified in the [Terminal Services] note... [or] a DirectoryNotFoundException may be thrown".
///
/// Lives under <c>Platform/Windows/</c> (mirroring <c>ServiceConsole</c>'s own split) rather than as
/// a flat file, even though there's no real Linux/macOS implementation yet -- see
/// <c>Platform/Linux/SingletonGuard.cs</c> and <c>Platform/Neutral/SingletonGuard.cs</c>, both
/// no-ops for now (see issue #27's "Not doing" list; the likely eventual real Linux mechanism is a
/// `systemd --user` service, the closest analog to the scheduled task this guards alongside -- both
/// "start at login" and "trigger on demand", unlike cron's periodic-only model). A genuinely
/// cross-platform version of *this specific class* would likely just drop the "Local\" prefix on
/// non-Windows (<c>OperatingSystem.IsWindows()</c>) rather than needing a different primitive
/// entirely, since named <see cref="Mutex"/> itself already works fine on Linux.
///
/// "Local\", not "Global\": a "Global\" named object needs <c>SeCreateGlobalPrivilege</c>, not
/// guaranteed for every account this might run under, while "Local\" (scoped to the current logon
/// session) is available to any user and is already sufficient for the actual threat this guards
/// against -- the scheduled task and any Desk-triggered auto-start both always run under the same
/// interactively-logged-on user's own session (see <c>installer/Package.wxs</c>'s
/// <c>/ru "[LogonUser]" /it</c>), never a different session.
/// </summary>
internal sealed class SingletonGuard : IDisposable
{
    internal const string DefaultMutexName = @"Local\Desk Tools Service";

    private readonly Mutex _mutex;

    private SingletonGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Tries to acquire the guard, waiting up to <paramref name="timeout"/> (default: none -- an
    /// already-running instance isn't going away on its own, so there's no reason to wait for it).
    /// Returns null if another instance already holds it -- the caller should exit cleanly, not
    /// treat that as an error. If the previous holder crashed without releasing it,
    /// <see cref="Mutex.WaitOne(TimeSpan)"/> throws <see cref="AbandonedMutexException"/> but still
    /// hands over ownership -- <paramref name="abandoned"/> reports whether that happened, so the
    /// caller can log a warning without treating it as a failure. No separate crash-detection scheme
    /// needed beyond this -- a named <see cref="Mutex"/> already gives it for free.
    /// </summary>
    public static SingletonGuard? TryAcquire(out bool abandoned, string mutexName = DefaultMutexName, TimeSpan? timeout = null)
    {
        abandoned = false;
        var mutex = new Mutex(initiallyOwned: false, mutexName, out _);

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout ?? TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            abandoned = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new SingletonGuard(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
