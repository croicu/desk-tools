using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Secrets;

/// <summary>
/// The "select your KeePass database" dialog, via Win32 <c>GetOpenFileNameW</c> -- P/Invoke only, no
/// COM interop and no WinForms/WPF (issue #50). Runs on its own STA thread (the shell extensions a
/// common dialog loads need one), owned by a hidden topmost window so a background, elevated process
/// still gets a visible dialog (it may not be allowed to take the foreground, so it must at least be
/// on top). <c>OFN_NOCHANGEDIR</c> keeps the process from pinning whichever directory the user browsed
/// through (the same class of lock <c>DevRedirect</c> avoids), and <c>OFN_DONTADDTORECENT</c> keeps an
/// elevated process out of the user's recent-files list. Not DPI-aware; English only.
///
/// Lives under <c>Platform/Windows/</c>; the Linux/Neutral variants are placeholders that throw (see
/// issue #51).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FilePicker : IFilePicker
{
    private const string Category = LogCategory.Secrets;

    // 32768 characters: the longest path Windows allows, so a long OneDrive path can't be truncated.
    private const int FileBufferChars = 32768;

    private const uint OfnReadOnlyHidden = 0x00000004;
    private const uint OfnNoChangeDir = 0x00000008;
    private const uint OfnPathMustExist = 0x00000800;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnExplorer = 0x00080000;
    private const uint OfnDontAddToRecent = 0x02000000;

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;

    public string? Pick(string title, string filterName, string extension)
    {
        Logger.Info($"secrets: showing the file dialog '{title}'.", Category);

        var state = new PickState(title, filterName, extension);

        var thread = new Thread(RunDialog) { IsBackground = true, Name = "file-picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(state);
        thread.Join();

        if (state.Error is not null)
        {
            throw state.Error;
        }

        Logger.Info(state.Path is null ? "secrets: the file dialog was cancelled." : $"secrets: file chosen: '{state.Path}'.", Category);
        return state.Path;
    }

    private static void RunDialog(object? argument)
    {
        var state = (PickState)argument!;

        // [STA] only makes the runtime CoInitialize this thread. The common dialog's shell pieces (the
        // file-name box's autocomplete, the folder view) expect a full OLE apartment, as a UI thread
        // that called Application.Run has -- without it the process can crash natively as soon as the
        // user types in the file-name box.
        var oleInitialized = NativeMethods.OleInitialize(IntPtr.Zero) >= 0;

        var owner = IntPtr.Zero;
        var file = IntPtr.Zero;
        var filter = IntPtr.Zero;
        var title = IntPtr.Zero;
        var defaultExtension = IntPtr.Zero;
        try
        {
            owner = NativeMethods.CreateWindowEx(WsExTopmost | WsExToolWindow, "STATIC", string.Empty, WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            file = Marshal.AllocHGlobal(FileBufferChars * sizeof(char));
            for (var i = 0; i < FileBufferChars; i++)
            {
                Marshal.WriteInt16(file, i * sizeof(char), 0);
            }

            // The filter is a list of NUL-separated strings ending in a double NUL, which a marshaled
            // .NET string can't carry (it stops at the first NUL) -- so it goes in as a raw buffer.
            filter = Marshal.StringToHGlobalUni($"{state.FilterName} (*.{state.Extension})\0*.{state.Extension}\0All files (*.*)\0*.*\0");
            title = Marshal.StringToHGlobalUni(state.Title);
            defaultExtension = Marshal.StringToHGlobalUni(state.Extension);

            var dialog = new NativeMethods.OpenFileName
            {
                StructSize = Marshal.SizeOf<NativeMethods.OpenFileName>(),
                Owner = owner,
                Filter = filter,
                FilterIndex = 1,
                File = file,
                MaxFile = FileBufferChars,
                Title = title,
                DefaultExtension = defaultExtension,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnReadOnlyHidden | OfnNoChangeDir | OfnDontAddToRecent,
            };

            if (NativeMethods.GetOpenFileName(ref dialog))
            {
                state.Path = Marshal.PtrToStringUni(file);
            }
            else
            {
                // Zero means the user cancelled; anything else is a real failure.
                var code = NativeMethods.CommDlgExtendedError();
                if (code != 0)
                {
                    state.Error = new AppError($"could not show the file dialog (CommDlgExtendedError 0x{code:X}).", Category);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            state.Error = new AppError($"the file dialog failed: {error.Message}", Category);
        }
        finally
        {
            if (owner != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(owner);
            }

            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(defaultExtension);

            if (oleInitialized)
            {
                NativeMethods.OleUninitialize();
            }
        }
    }

    private sealed class PickState
    {
        internal PickState(string title, string filterName, string extension)
        {
            Title = title;
            FilterName = filterName;
            Extension = extension;
        }

        internal string Title { get; }

        internal string FilterName { get; }

        internal string Extension { get; }

        internal string? Path { get; set; }

        internal Exception? Error { get; set; }
    }

    private static class NativeMethods
    {
        // Field order and types must match the Win32 OPENFILENAMEW exactly (152 bytes on x64). Every
        // string is an IntPtr to a buffer this class owns, so nothing is marshaled behind its back.
        [StructLayout(LayoutKind.Sequential)]
        internal struct OpenFileName
        {
            internal int StructSize;
            internal IntPtr Owner;
            internal IntPtr Instance;
            internal IntPtr Filter;
            internal IntPtr CustomFilter;
            internal int MaxCustomFilter;
            internal int FilterIndex;
            internal IntPtr File;
            internal int MaxFile;
            internal IntPtr FileTitle;
            internal int MaxFileTitle;
            internal IntPtr InitialDir;
            internal IntPtr Title;
            internal uint Flags;
            internal ushort FileOffset;
            internal ushort FileExtension;
            internal IntPtr DefaultExtension;
            internal IntPtr CustomData;
            internal IntPtr Hook;
            internal IntPtr TemplateName;
            internal IntPtr Reserved;
            internal int ReservedDword;
            internal int FlagsEx;
        }

        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", SetLastError = true)]
        internal static extern bool GetOpenFileName(ref OpenFileName dialog);

        [DllImport("comdlg32.dll")]
        internal static extern uint CommDlgExtendedError();

        [DllImport("ole32.dll")]
        internal static extern int OleInitialize(IntPtr reserved);

        [DllImport("ole32.dll")]
        internal static extern void OleUninitialize();

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr window);
    }
}
