using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Launcher;

/// <summary>
/// The connection to Service was refused: nothing is listening, i.e. Service is not running. A distinct type
/// (still an <see cref="AppError"/>, so everything that already handles one keeps working) so a caller can tell
/// this ordinary situation from a real failure: <see cref="Starter.EnsureRunning"/> answers it by starting
/// Service, and <c>desk shutdown</c> treats it as "nothing to do", not an error.
/// </summary>
public sealed class NotRunningError : AppError
{
    internal NotRunningError(string message, string category)
        : base(message, category)
    {
    }
}
