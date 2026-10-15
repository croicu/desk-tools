using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Desk;

/// <summary>
/// The connection to Service was refused: nothing is listening, i.e. Service is not running. A distinct type
/// (still an <see cref="AppError"/>, so everything that already handles one -- the auto-start retry of
/// <c>ping</c> and <c>mcp</c> -- keeps working) so a caller can tell this ordinary situation from a real
/// failure: <c>desk shutdown</c> treats it as "nothing to do", not an error.
/// </summary>
internal sealed class ServiceNotRunningError : AppError
{
    internal ServiceNotRunningError(string message, string category)
        : base(message, category)
    {
    }
}
