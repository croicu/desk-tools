using System.Runtime.InteropServices;

namespace Croicu.Desk.Tools.Service;

// DllImport rather than LibraryImport: the source-generated marshaling for a bool return needs
// AllowUnsafeBlocks project-wide, not worth enabling unsafe code for this one P/Invoke call.
internal sealed class ServiceConsole : IConsole
{
    // ATTACH_PARENT_PROCESS: attaches to whichever console the immediate parent process owns, if
    // any, instead of always popping a brand new window -- e.g. a launcher/script process that
    // didn't inherit console handles down to us but does have a real console the user is watching.
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private bool _attached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public void EnsureConsole()
    {
        // Best-effort only: if there's no parent console to attach to, the process just runs with
        // no console at all -- no AllocConsole fallback. Acceptable here: this is a background
        // service, not a CLI tool that must always be able to report to *some* console.
        _attached = AttachConsole(AttachParentProcess);
    }

    public void ReleaseConsole()
    {
        // Only detach if we actually attached to a console -- there's nothing to free otherwise,
        // and freeing the parent shell's console promptly lets its prompt return/repaint correctly
        // rather than looking stuck.
        if (_attached)
        {
            FreeConsole();
        }
    }
}
