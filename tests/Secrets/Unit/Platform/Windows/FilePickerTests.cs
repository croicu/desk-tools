using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Croicu.Desk.Tools.Secrets.Tests.Unit;

/// <summary>
/// Drives the <b>real</b> common file dialog: starts <see cref="FilePicker.Pick"/> on a task, finds the
/// dialog by its (unique) title, then does what a user would -- types the file name into the "File
/// name" box (found by walking the dialog's children: ComboBoxEx32 -> ComboBox -> Edit) and presses
/// Open (WM_COMMAND IDOK), or presses Cancel. A real dialog appears briefly, and two at once would be
/// found ambiguously by title, so: tagged Integration (excluded from the default run/CI) and not
/// parallelized. Only compiled in for win-x64.
///
/// The name is typed with posted WM_CHAR messages, never a cross-thread WM_SETTEXT: setting that
/// box's text from another thread crashed the process natively (it is an auto-completing combo box),
/// whereas posted keystrokes -- what a real user generates -- do not.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class FilePickerTests
{
    private const uint WmChar = 0x0102;
    private const uint WmCommand = 0x0111;
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(20);

    private static string UniqueTitle() => $"FilePickerTests-{Guid.NewGuid():N}";

    private static Task<string?> StartPicking(string title)
    {
        string? Pick() => new FilePicker().Pick(title, "KeePass databases", "kdbx");
        return Task.Run(Pick);
    }

    private static IntPtr WaitForDialog(string title)
    {
        var deadline = DateTime.UtcNow + BoundedWait;
        while (DateTime.UtcNow < deadline)
        {
            var window = NativeMethods.FindWindow("#32770", title);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the file dialog never appeared.");
        return IntPtr.Zero;
    }

    private static IntPtr FindFileNameEdit(IntPtr dialog)
    {
        var comboEx = NativeMethods.FindWindowEx(dialog, IntPtr.Zero, "ComboBoxEx32", null);
        var combo = NativeMethods.FindWindowEx(comboEx, IntPtr.Zero, "ComboBox", null);
        return NativeMethods.FindWindowEx(combo, IntPtr.Zero, "Edit", null);
    }

    private static void Type(IntPtr edit, string text)
    {
        foreach (var character in text)
        {
            NativeMethods.PostMessage(edit, WmChar, new IntPtr(character), IntPtr.Zero);
            Thread.Sleep(10);
        }

        // Let the auto-complete drop-down settle before the next action.
        Thread.Sleep(500);
    }

    /// <summary>
    /// Presses a button asynchronously. A synchronous SendMessage would block this thread for as long
    /// as the dialog sits in a modal message box ("file not found", say) it opened in response.
    /// </summary>
    private static void Click(IntPtr dialog, int id)
    {
        NativeMethods.PostMessage(dialog, WmCommand, new IntPtr(id), IntPtr.Zero);
    }

    /// <summary>
    /// If the dialog has a modal message box open, returns its text and dismisses it; otherwise null.
    /// Keeps a failing test from leaving a window (and a blocked thread) behind, and says why.
    /// </summary>
    private static string? DismissMessageBox(IntPtr dialog)
    {
        var popup = NativeMethods.GetLastActivePopup(dialog);
        if (popup == IntPtr.Zero || popup == dialog)
        {
            return null;
        }

        var text = new StringBuilder(512);
        var label = NativeMethods.FindWindowEx(popup, IntPtr.Zero, "Static", null);
        while (label != IntPtr.Zero)
        {
            var part = new StringBuilder(512);
            NativeMethods.GetWindowText(label, part, part.Capacity);
            if (part.Length > 0)
            {
                text.Append(part).Append(' ');
            }

            label = NativeMethods.FindWindowEx(popup, label, "Static", null);
        }

        NativeMethods.PostMessage(popup, WmCommand, new IntPtr(IdOk), IntPtr.Zero);
        return text.ToString().Trim();
    }

    private static string NewKdbx()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"file-picker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Secrets.kdbx");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [TestMethod]
    public void Pick_Cancel_ReturnsNull()
    {
        var title = UniqueTitle();
        var picking = StartPicking(title);
        var dialog = WaitForDialog(title);

        Click(dialog, IdCancel);

        Assert.IsTrue(picking.Wait(BoundedWait));
        Assert.IsNull(picking.Result);
    }

    [TestMethod]
    public void Pick_FileNameTypedThenOpen_ReturnsItsFullPath()
    {
        var file = NewKdbx();
        var title = UniqueTitle();
        var picking = StartPicking(title);
        var dialog = WaitForDialog(title);

        // The window exists well before its controls have finished initializing (the folder view and
        // the file-name box load through the shell); keystrokes sent earlier are silently dropped.
        Thread.Sleep(2000);

        var edit = FindFileNameEdit(dialog);
        Assert.AreNotEqual(IntPtr.Zero, edit, "could not find the dialog's File name box.");
        Type(edit, file);

        var typed = new StringBuilder(1024);
        NativeMethods.GetWindowText(edit, typed, typed.Capacity);
        if (!string.Equals(typed.ToString(), file, StringComparison.OrdinalIgnoreCase))
        {
            Click(dialog, IdCancel);
            picking.Wait(BoundedWait);
            Assert.Fail($"the file name box held '{typed}' instead of '{file}': keystrokes were dropped or altered (auto-complete?).");
        }

        Click(dialog, IdOk);

        if (!picking.Wait(BoundedWait))
        {
            // Don't leave the dialog (and its thread) behind if the click didn't take.
            var why = DismissMessageBox(dialog);
            var shown = new StringBuilder(1024);
            NativeMethods.GetWindowText(edit, shown, shown.Capacity);
            Click(dialog, IdCancel);
            picking.Wait(BoundedWait);
            Assert.Fail($"the dialog did not close after Open; message box: '{why ?? "(none)"}'; file name box held: '{shown}' (wanted '{file}').");
        }

        Assert.AreEqual(file, picking.Result, ignoreCase: true);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetLastActivePopup(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    }
}
