namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Only compiled in when the test project itself is built for win-x64 (see
/// tests/Service/Service.Tests.csproj's Platform/-selection ItemGroups, mirroring
/// src/Service/Service.csproj's own) -- run explicitly with `dotnet test -r win-x64` to exercise
/// these; the default `dotnet test` (no RID) instead compiles
/// Platform/Neutral/SingletonGuardTests.cs, which tests the no-op variant's contract instead. A
/// GUID-suffixed mutex name per test (never the real <see cref="SingletonGuard.DefaultMutexName"/>)
/// so parallel test methods (MSTestSettings.cs's method-level parallelization) never collide with
/// each other or with a real running Service instance's own mutex.
/// </summary>
[TestClass]
public sealed class SingletonGuardTests
{
    private static string UniqueMutexName() => $@"Local\SingletonGuardTests-{Guid.NewGuid():N}";

    [TestMethod]
    public void TryAcquire_NoExistingHolder_Succeeds()
    {
        var mutexName = UniqueMutexName();

        using var guard = SingletonGuard.TryAcquire(out var abandoned, mutexName);

        Assert.IsNotNull(guard);
        Assert.IsFalse(abandoned);
    }

    [TestMethod]
    public void TryAcquire_AlreadyHeld_ReturnsNull()
    {
        var mutexName = UniqueMutexName();
        using var first = SingletonGuard.TryAcquire(out _, mutexName);
        Assert.IsNotNull(first);

        // Must attempt the second acquisition on a different thread: named Mutex ownership in
        // .NET/Windows is tracked per-thread, not per-Mutex-object-instance or per-process, so the
        // same thread reacquiring a mutex it already owns always succeeds (reentrant) -- if both
        // attempts ran on this test method's own thread, that reentrancy would give a false pass
        // here. A real second process is always a genuinely different thread, so this is what
        // actually exercises contention.
        SingletonGuard? second = null;
        var abandoned = false;
        var thread = new Thread(() => second = SingletonGuard.TryAcquire(out abandoned, mutexName));
        thread.Start();
        thread.Join();

        Assert.IsNull(second);
        Assert.IsFalse(abandoned);
    }

    [TestMethod]
    public void TryAcquire_AfterRelease_CanBeReacquired()
    {
        var mutexName = UniqueMutexName();
        var first = SingletonGuard.TryAcquire(out _, mutexName);
        Assert.IsNotNull(first);
        first.Dispose();

        using var second = SingletonGuard.TryAcquire(out var abandoned, mutexName);

        Assert.IsNotNull(second);
        Assert.IsFalse(abandoned);
    }

    [TestMethod]
    public void TryAcquire_AbandonedByPreviousHolder_StillAcquiresAndReportsAbandoned()
    {
        var mutexName = UniqueMutexName();

        // Acquire on a background thread that ends without releasing -- the CLR tracks named Mutex
        // ownership per-thread, so the thread ending (not just a guard object going out of scope)
        // is what makes the OS treat it as abandoned.
        var acquiredOnBackgroundThread = new ManualResetEventSlim(initialState: false);
        var thread = new Thread(() =>
        {
            var mutex = new Mutex(initiallyOwned: false, mutexName, out _);
            mutex.WaitOne();
            acquiredOnBackgroundThread.Set();
            // Deliberately no ReleaseMutex()/Dispose() -- the thread just ends, abandoning it.
        });
        thread.Start();
        Assert.IsTrue(acquiredOnBackgroundThread.Wait(TimeSpan.FromSeconds(5)), "Background thread did not acquire the mutex within the bounded wait.");
        thread.Join();

        using var guard = SingletonGuard.TryAcquire(out var abandoned, mutexName);

        Assert.IsNotNull(guard);
        Assert.IsTrue(abandoned);
    }
}
