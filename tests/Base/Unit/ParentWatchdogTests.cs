using System.Diagnostics;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

[TestClass]
public sealed class ParentWatchdogTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Start_NoParentPid_ReturnsNullAndNeverExits()
    {
        var exitCalls = 0;

        var thread = ParentWatchdog.Start(exit: _ => exitCalls++, parentPid: "");

        Assert.IsNull(thread);
        Assert.AreEqual(0, exitCalls);
    }

    [TestMethod]
    public void Start_UnparseableParentPid_ReturnsNullAndNeverExits()
    {
        var exitCalls = 0;

        var thread = ParentWatchdog.Start(exit: _ => exitCalls++, parentPid: "not-a-pid");

        Assert.IsNull(thread);
        Assert.AreEqual(0, exitCalls);
    }

    [TestMethod]
    public void Start_LiveParent_DoesNotExit()
    {
        var exitCalls = 0;

        // This test process is a parent that stays alive for the whole test.
        var thread = ParentWatchdog.Start(exit: _ => exitCalls++, parentPid: Environment.ProcessId.ToString());

        Assert.IsNotNull(thread);
        Assert.IsFalse(thread.Join(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, exitCalls);
    }

    [TestMethod]
    public void Start_ParentExited_ExitsWithZero()
    {
        // A short-lived real process stands in for a parent that has gone away. Whether the
        // watchdog sees it already gone at Start or catches it exiting afterward, exit(0) must run.
        int exitCode = -1;
        var exited = new ManualResetEventSlim(initialState: false);

        void Exit(int code)
        {
            exitCode = code;
            exited.Set();
        }

        using var parent = Process.Start(new ProcessStartInfo("dotnet", "--version") { UseShellExecute = false, RedirectStandardOutput = true })!;
        var pid = parent.Id;
        parent.WaitForExit();

        ParentWatchdog.Start(exit: Exit, parentPid: pid.ToString());

        Assert.IsTrue(exited.Wait(BoundedWait), "Watchdog did not call exit within the bounded wait.");
        Assert.AreEqual(0, exitCode);
    }
}
