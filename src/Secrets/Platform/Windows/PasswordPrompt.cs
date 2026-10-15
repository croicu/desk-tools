using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The master-password dialog, via Win32 <c>DialogBoxIndirectParam</c> over the in-memory template
/// from <see cref="DialogTemplate"/> -- P/Invoke only, no WinForms/WPF (issue #50). Modal, topmost, and
/// on its own STA thread, so the caller never owns a window and never blocks on it. Because Service is
/// an elevated process, this window is elevated too: a medium-integrity process can't script it or read
/// its edit box (UIPI), which is the point of asking here and not in Desk or Claude Code.
///
/// <b>The dialog never times out</b> (issue #66): it stays up until the user answers or cancels it, or
/// the cancellation token passed to <see cref="AskAsync"/> fires (Service shutting down). A timer that
/// closed it could close it while the person is typing. A tool launch that needs the answer simply waits
/// for it, however long that takes -- see <see cref="PromptedPassword"/>.
///
/// The password is read straight into pinned <c>char[]</c>/<c>byte[]</c> buffers, never a
/// <see cref="string"/>, the char buffer is zeroed, and the edit box is cleared before the dialog
/// closes. Known limits, accepted: not DPI-aware (Service is a hidden background process) and English
/// only.
///
/// Lives under <c>Platform/Windows/</c>; the Linux/Neutral variants are placeholders that throw (see
/// issue #51).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PasswordPrompt : IPasswordPrompt
{
    private const string Category = LogCategory.Secrets;

    private const int PasswordLimit = 1024;

    private const uint WmInitDialog = 0x0110;
    private const uint WmCommand = 0x0111;

    // Posted to the dialog by OnInitDialog and handled once it is on screen: see TakeForeground.
    private const uint WmTakeForeground = 0x8000 + 1;
    private const uint EmSetLimitText = 0x00C5;
    private const int GwlpUserData = -21;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    public Task<byte[]?> AskAsync(string databasePath, int attempt, int maxAttempts, CancellationToken cancellation)
    {
        Logger.Info($"secrets: asking for the master password for '{databasePath}' (attempt {attempt + 1} of {maxAttempts}).", Category);

        var state = new DialogState(DialogTemplate.Build(databasePath, attempt, maxAttempts), databasePath);

        // Registered before the thread starts so a token that is already cancelled still closes the
        // dialog (RequestCancel sets a flag that OnInitDialog checks once the window exists).
        state.CancellationRegistration = cancellation.Register(RequestCancel, state);

        var thread = new Thread(RunDialog) { IsBackground = true, Name = "password-prompt" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(state);

        return state.Completion.Task;
    }

    /// <summary>
    /// Closes the dialog on behalf of the cancellation token. Runs on whatever thread cancels, so it
    /// only sets a flag and posts a message (never touches the dialog directly). Either this sees the
    /// window (posts Cancel) or <see cref="OnInitDialog"/> sees the flag (ends itself) -- whichever
    /// happens second observes the other.
    /// </summary>
    private static void RequestCancel(object? argument)
    {
        var state = (DialogState)argument!;
        state.CancelRequested = true;

        var window = state.Window;
        if (window != IntPtr.Zero)
        {
            NativeMethods.PostMessage(window, WmCommand, new IntPtr(DialogTemplate.IdCancel), IntPtr.Zero);
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
            state.CancellationRegistration.Dispose();
            Finish(state);
        }
    }

    /// <summary>Completes the task the caller holds, exactly once, with whatever the dialog ended with.</summary>
    private static void Finish(DialogState state)
    {
        if (state.Error is not null)
        {
            state.Completion.TrySetException(state.Error);
        }
        else if (state.Outcome == DialogOutcome.Entered)
        {
            state.Completion.TrySetResult(state.Password);
        }
        else
        {
            Logger.Info($"secrets: the master password prompt for '{state.DatabasePath}' was cancelled.", Category);
            state.Completion.TrySetResult(null);
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
                case WmTakeForeground:
                    TakeForeground(window);
                    return new IntPtr(1);
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

        var edit = NativeMethods.GetDlgItem(window, DialogTemplate.IdPassword);
        NativeMethods.SendMessage(edit, EmSetLimitText, new IntPtr(PasswordLimit), IntPtr.Zero);

        // Publish the window first, then look for a cancel that arrived before it existed.
        state.Window = window;
        if (state.CancelRequested)
        {
            state.Outcome = DialogOutcome.Cancelled;
            End(window);
            return new IntPtr(1);
        }

        // A background process isn't allowed to take the foreground freely, so make the window topmost: it
        // must at least be visible. Taking the foreground itself has to wait until the dialog is on screen
        // (it is not yet, here), so it is posted to the dialog's own queue. Returning FALSE tells the dialog
        // manager that focus was already set here.
        NativeMethods.SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        NativeMethods.SetFocus(edit);
        NativeMethods.PostMessage(window, WmTakeForeground, IntPtr.Zero, IntPtr.Zero);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Makes the dialog the foreground window, with the keyboard on the password box, so what the user types
    /// goes into it (issue #76: they kept typing the password into whatever was in front). Service is
    /// started by Task Scheduler, not by anything the user is working in, so Windows' foreground lock
    /// refuses a plain <c>SetForegroundWindow</c> (it only flashes the taskbar button). The documented way
    /// round it is to attach this thread's input to the foreground window's thread for the duration of the
    /// call, which makes the two share input state; best effort -- if it still does not work the window is
    /// topmost and visible, and the user can click it.
    /// </summary>
    private static void TakeForeground(IntPtr window)
    {
        var thisThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = 0u;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        }

        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
            NativeMethods.SetFocus(NativeMethods.GetDlgItem(window, DialogTemplate.IdPassword));
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        var taken = NativeMethods.GetForegroundWindow() == window;
        if (taken)
        {
            Logger.Diagnostic("secrets: the master password dialog has the foreground.", Category);
        }
        else
        {
            Logger.Warning("secrets: the master password dialog could not take the foreground; it is on top, click it to type.", Category);
        }
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
    }

    private sealed class DialogState
    {
        private long _window;
        private volatile bool _cancelRequested;

        internal DialogState(byte[] template, string databasePath)
        {
            Template = template;
            DatabasePath = databasePath;
        }

        internal byte[] Template { get; }

        internal string DatabasePath { get; }

        internal TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenRegistration CancellationRegistration { get; set; }

        /// <summary>The dialog's window handle, set once it exists (read from the cancelling thread).</summary>
        internal IntPtr Window
        {
            get => new(Volatile.Read(ref _window));
            set => Volatile.Write(ref _window, value.ToInt64());
        }

        internal bool CancelRequested
        {
            get => _cancelRequested;
            set => _cancelRequested = value;
        }

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

        // Posted, not sent: this is called from the cancelling thread, which must never block on (or
        // wait for) the dialog's own thread.
        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

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
