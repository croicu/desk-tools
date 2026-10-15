using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service.Tests.Unit;

/// <summary>
/// Runs the <b>real</b> <c>GenerateMcpRegistryEntry</c> target from <c>src/Directory.Build.targets</c>
/// on a throwaway project and checks the registry fragment it writes -- including the optional
/// <c>secrets</c> array rendered from <c>&lt;McpSecret&gt;</c> items (issue #50, slice 4a). No shipped
/// tool declares a secret yet, so this is the only automated coverage of that rendering. The generated
/// file is then fed to <see cref="McpToolLauncher.ParseFragment"/>, so a mismatch between what the
/// build writes and what Service reads shows up here. Offline: the only process spawned is
/// <c>dotnet msbuild</c>, already a hard dependency of running these tests.
/// </summary>
[TestClass]
public sealed class RegistryGenerationTests
{
    private const string ToolName = "fixture-tool";

    [TestCleanup]
    public void Cleanup() => Logger.SetLogger(null);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Directory.Build.targets")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not find the repo root (src/Directory.Build.targets) above the test's output folder.");
    }

    /// <summary>Builds a fixture project with the given &lt;McpSecret&gt; items and runs the generate target.</summary>
    private static (ProcessResult Result, string RegistryPath) Generate(string secretItems)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"registry-generation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var outDir = Path.Combine(dir, "out") + Path.DirectorySeparatorChar;
        var targets = Path.Combine(RepoRoot(), "src", "Directory.Build.targets");
        var projectPath = Path.Combine(dir, "fixture.proj");
        File.WriteAllText(projectPath, $"""
            <Project>
              <PropertyGroup>
                <McpToolName>{ToolName}</McpToolName>
                <TargetFileName>Fixture.dll</TargetFileName>
                <OutDir>{outDir}</OutDir>
              </PropertyGroup>
              <ItemGroup>
            {secretItems}
              </ItemGroup>
              <Import Project="{targets}" />
            </Project>
            """);

        var result = new ProcessRunner().Run("dotnet", ["msbuild", projectPath, "-t:GenerateMcpRegistryEntry", "-nologo", "-v:minimal"], ReadOnlySpan<byte>.Empty);
        return (result, Path.Combine(outDir, "mcp-registry", $"{ToolName}.json"));
    }

    private static string Output(ProcessResult result) => Encoding.UTF8.GetString(result.Stdout) + result.Stderr;

    [TestMethod]
    public void Generate_NoDeclarations_WritesTheSameFragmentAsBefore()
    {
        var (result, registryPath) = Generate(string.Empty);

        Assert.AreEqual(0, result.ExitCode, Output(result));
        Assert.AreEqual(
            """{"name": "fixture-tool", "type": "stdio", "command": "dotnet", "args": ["Fixture.dll"], "env": {}}""",
            File.ReadAllText(registryPath).Trim(),
            "a tool that declares no secrets must get a fragment byte-identical to the pre-secrets one.");
    }

    [TestMethod]
    public void Generate_WithDeclarations_RendersASecretsArrayThatTheLauncherParsesBack()
    {
        var (result, registryPath) = Generate("""
                <McpSecret Include="NTFY_TOPIC" Entry="ntfy" Key="desk-pager" />
                <McpSecret Include="OTHER_ONE" Entry="Group/Sub entry" Key="k2" />
            """);

        Assert.AreEqual(0, result.ExitCode, Output(result));
        var json = File.ReadAllText(registryPath);
        Assert.Contains(""""secrets": [{"env": "NTFY_TOPIC", "entry": "ntfy", "key": "desk-pager"}, {"env": "OTHER_ONE", "entry": "Group/Sub entry", "key": "k2"}]"""", json);

        var fragment = McpToolLauncher.ParseFragment(json);
        Assert.HasCount(2, fragment.Secrets);
        Assert.AreEqual(new McpToolLauncher.McpSecretDeclaration("NTFY_TOPIC", "ntfy", "desk-pager"), fragment.Secrets[0]);
        Assert.AreEqual(new McpToolLauncher.McpSecretDeclaration("OTHER_ONE", "Group/Sub entry", "k2"), fragment.Secrets[1]);
        Assert.AreEqual("dotnet", fragment.Command);
    }

    [TestMethod]
    [DataRow("""<McpSecret Include="X" Key="k" />""", "Entry")]
    [DataRow("""<McpSecret Include="X" Entry="e" />""", "Key")]
    [DataRow("""<McpSecret Include="X" Entry="has&quot;quote" Key="k" />""", "double quote")]
    [DataRow("""<McpSecret Include="X" Entry="e" Key="back\slash" />""", "backslash")]
    public void Generate_InvalidDeclaration_FailsTheBuildNamingTheProblem(string item, string expectedInMessage)
    {
        var (result, registryPath) = Generate(item);

        Assert.AreNotEqual(0, result.ExitCode, "an invalid declaration is the developer's own mistake and must fail the build.");
        Assert.Contains("Invalid McpSecret", Output(result));
        Assert.Contains(expectedInMessage, Output(result));
        Assert.IsFalse(File.Exists(registryPath), "no fragment should be written when the declarations are invalid.");
    }
}
