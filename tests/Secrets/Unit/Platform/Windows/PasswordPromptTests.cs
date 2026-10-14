using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Drives the <b>real</b> dialog: starts <see cref="PasswordPrompt.Ask"/> on a task, finds the window
/// by its title, then sends it the same messages a click would (WM_SETTEXT into the edit box,
/// WM_COMMAND IDOK/IDCANCEL). Real windows appear briefly, and two dialogs at once would be found by
/// title ambiguously, so: tagged Integration (excluded from the default run/CI), and not parallelized.
/// Only compiled in for win-x64.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class PasswordPromptTests
{
    private const string Db = @"C:\data\my.kdbx";
    private const uint WmSetText = 0x000C;
    private const uint WmCommand = 0x0111;
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    private static IntPtr WaitForDialog()
    {
        var deadline = DateTime.UtcNow + BoundedWait;
        while (DateTime.UtcNow < deadline)
        {
            var window = NativeMethods.FindWindow("#32770", DialogTemplate.Title);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            Thread.Sleep(25);
        }

        Assert.Fail("the password dialog never appeared.");
        return IntPtr.Zero;
    }

    private static string ItemText(IntPtr window, int id)
    {
        var text = new StringBuilder(512);
        NativeMethods.GetDlgItemText(window, id, text, text.Capacity);
        return text.ToString();
    }

    private static void Click(IntPtr window, int id)
    {
        NativeMethods.SendMessage(window, WmCommand, new IntPtr(id), IntPtr.Zero);
    }

    private static void Type(IntPtr window, string password)
    {
        var edit = NativeMethods.GetDlgItem(window, DialogTemplate.IdPassword);
        NativeMethods.SendMessageText(edit, WmSetText, IntPtr.Zero, password);
    }

    private static Task<byte[]?> StartAsking(PasswordPrompt prompt, int attempt = 0)
    {
        byte[]? Ask() => prompt.Ask(Db, attempt, 3);
        return Task.Run(Ask);
    }

    [TestMethod]
    public void Ask_PasswordThenOk_ReturnsItsUtf8Bytes()
    {
        var asking = StartAsking(new PasswordPrompt());
        var window = WaitForDialog();

        Type(window, "s3cret-é");
        Click(window, DialogTemplate.IdOk);

        Assert.IsTrue(asking.Wait(BoundedWait));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("s3cret-é"), asking.Result);
    }

    [TestMethod]
    public void Ask_Cancel_ReturnsNull()
    {
        var asking = StartAsking(new PasswordPrompt());
        var window = WaitForDialog();

        Type(window, "typed-but-cancelled");
        Click(window, DialogTemplate.IdCancel);

        Assert.IsTrue(asking.Wait(BoundedWait));
        Assert.IsNull(asking.Result);
    }

    [TestMethod]
    public void Ask_NobodyAnswers_TimesOutAndReturnsNull()
    {
        var asking = StartAsking(new PasswordPrompt(TimeSpan.FromSeconds(1)));

        Assert.IsTrue(asking.Wait(BoundedWait), "the dialog did not close itself after its timeout.");
        Assert.IsNull(asking.Result);
    }

    [TestMethod]
    public void Ask_EmptyPasswordThenOk_KeepsTheDialogOpen()
    {
        var asking = StartAsking(new PasswordPrompt());
        var window = WaitForDialog();

        Click(window, DialogTemplate.IdOk);
        Thread.Sleep(300);

        Assert.IsFalse(asking.IsCompleted, "an empty password must not close the dialog.");
        Assert.IsTrue(NativeMethods.IsWindow(window));
        Click(window, DialogTemplate.IdCancel);
        Assert.IsTrue(asking.Wait(BoundedWait));
    }

    [TestMethod]
    public void Ask_Retry_ShowsTheDatabaseAndTheAttemptNumber()
    {
        var asking = StartAsking(new PasswordPrompt(), attempt: 1);
        var window = WaitForDialog();

        Assert.AreEqual(Db, ItemText(window, DialogTemplate.IdPath));
        Assert.AreEqual("Incorrect password. Attempt 2 of 3.", ItemText(window, DialogTemplate.IdAttempt));
        Click(window, DialogTemplate.IdCancel);
        Assert.IsTrue(asking.Wait(BoundedWait));
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetDlgItemText(IntPtr dialog, int id, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessageText(IntPtr window, uint message, IntPtr wParam, string text);
    }
}
