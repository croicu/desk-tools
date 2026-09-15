namespace Croicu.Desk.Tools.Service;

// Compiled in whenever RuntimeIdentifier isn't win-x64 or linux-x64 -- notably, when it's unset
// entirely (a plain dotnet build/test/run, or the IDE's own design-time build for IntelliSense).
// A no-op here, same reasoning as Platform/Linux/SingletonGuard.cs: with no RID to say otherwise,
// there's no real single-instance mechanism to provide, so always "acquiring" successfully is the
// safe default -- same shape ServiceConsole's own Neutral variant follows.
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
