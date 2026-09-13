namespace Croicu.Desk.Tools.Mocks;

/// <summary>
/// Redirects Console.Out for the duration of one action and returns what it wrote. Console.Out is
/// a genuinely process-wide BCL property with no per-context equivalent, so any test using this
/// must not run in parallel with another test that also does -- mark the containing class (or
/// method) [DoNotParallelize].
/// </summary>
public static class ConsoleCapture
{
    public static string CaptureOut(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
