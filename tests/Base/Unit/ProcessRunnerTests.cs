using System.Text;

namespace Croicu.Desk.Tools.Base.Tests.Unit;

/// <summary>
/// Runs the real <c>dotnet</c> host (always present where these tests run, and on PATH the same way
/// <c>HandleDuplicatorTests</c>/<c>ToolLauncher</c> tests already rely on). stdin delivery isn't
/// covered here -- no cross-platform program just echoes stdin -- it's exercised for real by
/// <c>tests/Secrets/Integration</c>, where <c>keepassxc-cli</c> receives its password that way.
/// </summary>
[TestClass]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public void Run_DotnetVersion_ReturnsExitZeroAndStdout()
    {
        var result = new ProcessRunner().Run("dotnet", ["--version"], ReadOnlySpan<byte>.Empty);

        Assert.AreEqual(0, result.ExitCode);
        var text = Encoding.UTF8.GetString(result.Stdout).Trim();
        Assert.IsTrue(Version.TryParse(text.Split('-')[0], out _), $"expected a version, got '{text}'.");
    }

    [TestMethod]
    public void Run_UnknownCommand_ReturnsNonZeroExit()
    {
        var result = new ProcessRunner().Run("dotnet", ["definitely-not-a-dotnet-command"], ReadOnlySpan<byte>.Empty);

        Assert.AreNotEqual(0, result.ExitCode);
    }

    [TestMethod]
    public void Run_ExecutableMissing_ThrowsAppErrorNamingIt()
    {
        var error = Assert.ThrowsExactly<AppError>(() => new ProcessRunner().Run("no-such-executable-xyz", [], ReadOnlySpan<byte>.Empty));

        Assert.Contains("no-such-executable-xyz", error.Message);
    }

    [TestMethod]
    public void Run_OutputLargerThanInitialBuffer_IsReadInFull()
    {
        // A 16-byte starting buffer forces several grow-and-zero-the-old-buffer rounds on output
        // much longer than that; compare against the same command read with the default buffer.
        var expected = new ProcessRunner().Run("dotnet", ["--info"], ReadOnlySpan<byte>.Empty);
        var result = new ProcessRunner(initialBufferSize: 16).Run("dotnet", ["--info"], ReadOnlySpan<byte>.Empty);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsGreaterThan(16 * 4, result.Stdout.Length);
        CollectionAssert.AreEqual(expected.Stdout, result.Stdout);
    }
}
