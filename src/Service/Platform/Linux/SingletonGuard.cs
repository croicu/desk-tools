namespace Croicu.Desk.Tools.Service;

// Mirrors Platform/Windows/SingletonGuard.cs's class name/API shape so Program.cs's call site
// needs no #if/OperatingSystem.IsWindows() branch, same reasoning as ServiceConsole's own split.
//
// No real cross-process guard yet: a no-op that always "acquires" successfully. Not a fundamental
// platform limitation (named Mutex itself works fine on Linux, see the Windows variant's own
// remarks) -- there's just nothing to guard *against* yet, since Service has no Linux-equivalent
// resident-process launch mechanism at all (the closest analog to the scheduled task this would
// pair with is a `systemd --user` service; cron doesn't fit, it's periodic-only, not "start at
// login and trigger on demand"). Revisit together with that (see issue #27's "Not doing" list).
internal sealed class SingletonGuard : IDisposable
{
    internal const string DefaultMutexName = @"Local\Desk Tools Service";

    private SingletonGuard()
    {
    }

    public static SingletonGuard? TryAcquire(out bool abandoned, string mutexName = DefaultMutexName, TimeSpan? timeout = null)
    {
        abandoned = false;
        return new SingletonGuard();
    }

    public void Dispose()
    {
    }
}
