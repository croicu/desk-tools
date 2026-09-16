using System.Runtime.InteropServices;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Hands a launched <see cref="ToolProcess"/>'s stdin/stdout pipe ends directly to another process
/// via Win32 <c>DuplicateHandle</c> (see issue #32), instead of Service staying in the loop as a
/// byte relay. Validated live via a throwaway spike before this was built: a real elevated process
/// duplicated a redirected pipe's handles into a real unelevated, already-running, unrelated
/// process, which then sent <c>ping</c> and read <c>ping</c> back through them directly -- proving
/// the technique crosses the actual privilege boundary this project needs it for (Service is
/// elevated, Desk isn't).
///
/// Lives under <c>Platform/Windows/</c> (mirroring <c>SingletonGuard</c>'s own split) since
/// <c>DuplicateHandle</c>/<c>OpenProcess</c> are Windows-only APIs with no cross-platform
/// equivalent (unlike named <see cref="Mutex"/>, which is why <c>SingletonGuard</c> itself could
/// stay a thin per-platform wrapper around one shared primitive) -- see
/// <c>Platform/Linux/HandleDuplicator.cs</c>/<c>Platform/Neutral/HandleDuplicator.cs</c>, both of
/// which throw a clear <see cref="AppError"/> for now rather than attempt a different mechanism
/// (e.g. <c>SCM_RIGHTS</c> over a Unix domain socket on Linux) -- out of scope here.
/// </summary>
internal static class HandleDuplicator
{
    /// <summary>
    /// Duplicates <paramref name="tool"/>'s stdin/stdout pipe handles into the process identified by
    /// <paramref name="targetProcessId"/>, then closes Service's own copies -- see
    /// <see cref="ToolProcess.DisownAfterHandoff"/>'s own remarks on why the caller must use that,
    /// not <see cref="ToolProcess.Dispose"/>, afterward. Returns the two duplicated handle values
    /// (valid only inside the target process's own handle table) as-is -- the caller decides how to
    /// serialize them (see <c>Host</c>'s <c>mcp</c> method: decimal text, not a JSON number, since a
    /// <c>HANDLE</c> is a pointer -- 8 bytes on x64 -- and a JSON number can't reliably round-trip
    /// that precision). Throws <see cref="AppError"/> on any failure: the target process doesn't
    /// exist or can't be opened for <c>PROCESS_DUP_HANDLE</c> access, or either duplication itself
    /// fails.
    /// </summary>
    internal static (long Stdin, long Stdout) Duplicate(ToolProcess tool, int targetProcessId)
    {
        var stdinHandle = ((FileStream)tool.StandardInput.BaseStream).SafeFileHandle;
        var stdoutHandle = ((FileStream)tool.StandardOutput.BaseStream).SafeFileHandle;

        var targetProcessHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_DUP_HANDLE, false, targetProcessId);
        if (targetProcessHandle == IntPtr.Zero)
        {
            throw new AppError($"could not open process {targetProcessId} to duplicate handles into it (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var currentProcess = NativeMethods.GetCurrentProcess();

            if (!NativeMethods.DuplicateHandle(currentProcess, stdinHandle.DangerousGetHandle(), targetProcessHandle, out var dupStdin, 0, false, NativeMethods.DUPLICATE_SAME_ACCESS))
            {
                throw new AppError($"could not duplicate the tool's stdin handle into process {targetProcessId} (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (!NativeMethods.DuplicateHandle(currentProcess, stdoutHandle.DangerousGetHandle(), targetProcessHandle, out var dupStdout, 0, false, NativeMethods.DUPLICATE_SAME_ACCESS))
            {
                throw new AppError($"could not duplicate the tool's stdout handle into process {targetProcessId} (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            // Relinquish Service's own copy of the write end -- the spike's own lesson (see issue
            // #30's discussion): the launched tool's stdin pipe won't see EOF (and so never exits)
            // until every write-end handle is closed, including this one, not just the target
            // process's own duplicated copy once it's done with the tool.
            tool.StandardInput.Close();

            return ((long)dupStdin, (long)dupStdout);
        }
        finally
        {
            NativeMethods.CloseHandle(targetProcessHandle);
        }
    }

    private static class NativeMethods
    {
        internal const uint PROCESS_DUP_HANDLE = 0x0040;
        internal const uint DUPLICATE_SAME_ACCESS = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
