namespace DeskTools.Base;

/// <summary>Equivalent of the Python template's <c>telemetry_session()</c> context manager -- flushes
/// and clears the logger's pending buffer on scope exit. Usage: <c>using var _ = new TelemetrySession();</c></summary>
public sealed class TelemetrySession : IDisposable
{
    public void Dispose()
    {
        Logger.Flush();
        Logger.Clear();
    }
}

public class AppError : Exception
{
    public TelemetryRecord Record { get; }

    public AppError(string message, string category = DiagnosticsCategories.General)
        : base(message)
    {
        Record = Logger.Log(TelemetryLevel.Warning, message, category);
    }
}

public sealed class TaskError : AppError
{
    public TaskError(string message)
        : base(message)
    {
    }
}

public sealed class SettingsError : AppError
{
    public SettingsError(string message)
        : base(message, DiagnosticsCategories.Settings)
    {
    }
}
