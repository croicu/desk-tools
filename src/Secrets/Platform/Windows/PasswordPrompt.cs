using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The master-password dialog, via Win32 <c>DialogBoxIndirectParam</c> over the in-memory template
/// from <see cref="DialogTemplate"/> -- P/Invoke only, no WinForms/WPF (issue #50). Modal, topmost, and
/// on its own STA thread so the calling pool thread never owns a window. Because Service is an
/// elevated process, this window is elevated too: a medium-integrity process can't script it or read
/// its edit box (UIPI), which is the point of asking here and not in Desk or Claude Code.
///
/// The password is read straight into pinned <c>char[]</c>/<c>byte[]</c> buffers, never a
/// <see cref="string"/>, the char buffer is zeroed, and the edit box is cleared before the dialog
/// closes. A <c>WM_TIMER</c> closes the dialog after the timeout (default two minutes), since a
/// <c>desk mcp</c> call is waiting on it. Known limits, accepted: not DPI-aware (Service is a hidden
/// background process) and English only.
///
/// Lives under <c>Platform/Windows/</c>; the Linux/Neutral variants are placeholders that throw (see
/// issue #51).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PasswordPrompt : IPasswordPrompt
{
    internal const int DefaultTimeoutSeconds = 120;

    private const string Category = LogCategory.Secrets;

    private const int PasswordLimit = 1024;
    private const int TimerId = 1;

    private const uint WmInitDialog = 0x0110;
    private const uint WmCommand = 0x0111;
    private const uint WmTimer = 0x0113;
    private const uint WmDestroy = 0x0002;
    private const uint EmSetLimitText = 0x00C5;
    private const int GwlpUserData = -21;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    private readonly TimeSpan _timeout;

    internal PasswordPrompt(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    public byte[]? Ask(string databasePath, int attempt, int maxAttempts)
    {
        Logger.Info($"secrets: asking for the master password for '{databasePath}' (attempt {attempt + 1} of {maxAttempts}).", Category);

        var state = new DialogState(DialogTemplate.Build(databasePath, attempt, maxAttempts), _timeout);

        var thread = new Thread(RunDialog) { IsBackground = true, Name = "password-prompt" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(state);
        thread.Join();

        if (state.Error is not null)
        {
            throw state.Error;
        }

        switch (state.Outcome)
        {
            case DialogOutcome.Entered:
                return state.Password;
            case DialogOutcome.TimedOut:
                Logger.Warning($"secrets: the master password prompt for '{databasePath}' timed out after {_timeout.TotalSeconds:F0}s.", Category);
                return null;
            default:
                Logger.Info($"secrets: the master password prompt for '{databasePath}' was cancelled.", Category);
                return null;
        }
    }

    private static void RunDialog(object? argument)
    {
        var state = (DialogState)argument!;
        var handle = GCHandle.Alloc(state);
        try
        {
            NativeMethods.DialogProc callback = DialogCallback;
            var result = NativeMethods.DialogBoxIndirectParam(IntPtr.Zero, state.Template, IntPtr.Zero, callback, GCHandle.ToIntPtr(handle));
            GC.KeepAlive(callback);

            if (result == -1 && state.Error is null)
            {
                state.Error = new AppError($"could not show the master password dialog (Win32 error {Marshal.GetLastWin32Error()}).", Category);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            state.Error = new AppError($"the master password dialog failed: {error.Message}", Category);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// The dialog procedure. Nothing may propagate out of it (it is called from native code), so any
    /// failure is parked on the state and the dialog is ended.
    /// </summary>
    private static IntPtr DialogCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                    return OnInitDialog(window, lParam);
                case WmCommand:
                    OnCommand(window, (int)((long)wParam & 0xFFFF));
                    return new IntPtr(1);
                case WmTimer:
                    if ((int)wParam == TimerId)
                    {
                        GetState(window).Outcome = DialogOutcome.TimedOut;
                        End(window);
                        return new IntPtr(1);
                    }

                    return IntPtr.Zero;
                case WmDestroy:
                    NativeMethods.KillTimer(window, TimerId);
                    return IntPtr.Zero;
                default:
                    return IntPtr.Zero;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            GetState(window).Error = new AppError($"the master password dialog failed: {error.Message}", Category);
            End(window);
            return new IntPtr(1);
        }
    }

    private static IntPtr OnInitDialog(IntPtr window, IntPtr lParam)
    {
        var state = (DialogState)GCHandle.FromIntPtr(lParam).Target!;
        NativeMethods.SetWindowLongPtr(window, GwlpUserData, lParam);

        NativeMethods.SetTimer(window, TimerId, (uint)state.Timeout.TotalMilliseconds, IntPtr.Zero);

        var edit = NativeMethods.GetDlgItem(window, DialogTemplate.IdPassword);
        NativeMethods.SendMessage(edit, EmSetLimitText, new IntPtr(PasswordLimit), IntPtr.Zero);

        // A hidden background process isn't allowed to take the foreground freely, so also make the
        // window topmost: it must at least be visible. Returning FALSE tells the dialog manager that
        // focus was already set here.
        NativeMethods.SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        NativeMethods.SetForegroundWindow(window);
        NativeMethods.SetFocus(edit);
        return IntPtr.Zero;
    }

    private static void OnCommand(IntPtr window, int id)
    {
        var state = GetState(window);

        if (id == DialogTemplate.IdOk)
        {
            var password = ReadPassword(window);
            if (password.Length == 0)
            {
                // An empty master password is never valid; keep the dialog open.
                NativeMethods.MessageBeep(0);
                return;
            }

            state.Password = password;
            state.Outcome = DialogOutcome.Entered;
            End(window);
        }
        else if (id == DialogTemplate.IdCancel)
        {
            state.Outcome = DialogOutcome.Cancelled;
            End(window);
        }
    }

    /// <summary>
    /// Copies the edit box's text into a pinned UTF-8 byte array and wipes every intermediate: the
    /// pinned char buffer is zeroed and the edit control's own text is cleared.
    /// </summary>
    private static byte[] ReadPassword(IntPtr window)
    {
        var edit = NativeMethods.GetDlgItem(window, DialogTemplate.IdPassword);
        var length = NativeMethods.GetWindowTextLength(edit);
        var chars = GC.AllocateArray<char>(length + 1, pinned: true);
        try
        {
            var copied = NativeMethods.GetWindowText(edit, ref chars[0], chars.Length);
            var bytes = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(chars, 0, copied), pinned: true);
            Encoding.UTF8.GetBytes(chars, 0, copied, bytes, 0);
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            NativeMethods.SetWindowText(edit, string.Empty);
        }
    }

    private static void End(IntPtr window)
    {
        NativeMethods.EndDialog(window, new IntPtr(1));
    }

    private static DialogState GetState(IntPtr window)
    {
        var pointer = NativeMethods.GetWindowLongPtr(window, GwlpUserData);
        return (DialogState)GCHandle.FromIntPtr(pointer).Target!;
    }

    private enum DialogOutcome
    {
        Cancelled,
        Entered,
        TimedOut,
    }

    private sealed class DialogState
    {
        internal DialogState(byte[] template, TimeSpan timeout)
        {
            Template = template;
            Timeout = timeout;
        }

        internal byte[] Template { get; }

        internal TimeSpan Timeout { get; }

        internal DialogOutcome Outcome { get; set; } = DialogOutcome.Cancelled;

        internal byte[]? Password { get; set; }

        internal Exception? Error { get; set; }
    }

    private static class NativeMethods
    {
        internal delegate IntPtr DialogProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr DialogBoxIndirectParam(IntPtr instance, byte[] template, IntPtr parent, DialogProc dialogFunction, IntPtr initParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EndDialog(IntPtr dialog, IntPtr result);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern UIntPtr SetTimer(IntPtr window, int id, uint elapseMilliseconds, IntPtr timerFunction);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool KillTimer(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool MessageBeep(uint type);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
        internal static extern int GetWindowTextLength(IntPtr window);

        // `ref char` (not a char[] or StringBuilder): the text is written straight into the caller's
        // pinned buffer, with no marshaling copy left behind that nobody could zero.
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, ref char buffer, int maxCount);

        [DllImport("user32.dll", EntryPoint = "SetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetWindowText(IntPtr window, string text);
    }
}
