using Croicu.Desk.Tools.Base;
using Microsoft.Win32.SafeHandles;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Only compiled in when the test project itself is built for win-x64 (see
/// tests/Service/Service.Tests.csproj's Platform/-selection ItemGroups) -- win-x64 is
/// tests/Directory.Build.props' own default RuntimeIdentifier now, so a plain `dotnet test` already
/// exercises these; an explicit `DESK_TOOLS_RID` override to something else instead compiles
/// Platform/Neutral/HandleDuplicatorTests.cs, which tests the "not supported" variant's contract
/// instead. Duplicates into this test process's own PID rather than a genuinely separate one --
/// enough to prove the mechanism/code path works; the real cross-privilege-boundary behavior
/// (elevated Service into unelevated Desk) was already validated manually via a throwaway spike
/// (see issue #32's own body) before this class was built.
/// </summary>
[TestClass]
public sealed class HandleDuplicatorTests
{
    [TestMethod]
    public void Duplicate_OwnProcess_ReturnsHandlesUsableInTargetProcess()
    {
        using var tool = ToolLauncher.Launch("dotnet", ["--version"], new Dictionary<string, string>(), AppContext.BaseDirectory);

        var (stdinHandle, stdoutHandle) = HandleDuplicator.Duplicate(tool, Environment.ProcessId);

        Assert.AreNotEqual(0L, stdinHandle);
        Assert.AreNotEqual(0L, stdoutHandle);

        // Duplicated into THIS process -- open the stdout copy directly and confirm it actually
        // carries the real child's output, proving the duplicated handle value is genuinely usable,
        // not just non-zero.
        using var duplicatedStdout = new SafeFileHandle((nint)stdoutHandle, ownsHandle: true);
        using var readStream = new FileStream(duplicatedStdout, FileAccess.Read);
        using var reader = new StreamReader(readStream);

        var output = reader.ReadLine();
        Assert.IsFalse(string.IsNullOrWhiteSpace(output));

        // Service's own copy of the write end was already closed by Duplicate itself -- this test
        // process owns the duplicated stdin copy instead; open+dispose it to release it cleanly
        // (dotnet --version doesn't read stdin, so there's nothing to write through it here).
        using var duplicatedStdin = new SafeFileHandle((nint)stdinHandle, ownsHandle: true);
    }

    [TestMethod]
    public void Duplicate_NonExistentTargetProcess_ThrowsAppError()
    {
        using var tool = ToolLauncher.Launch("dotnet", ["--version"], new Dictionary<string, string>(), AppContext.BaseDirectory);

        // A PID essentially guaranteed not to correspond to any running process.
        Assert.ThrowsExactly<AppError>(() => HandleDuplicator.Duplicate(tool, targetProcessId: 999_999));
    }
}
