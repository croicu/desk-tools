using System.Diagnostics;
using System.Text;
using Croicu.Desk.Tools.Base;

namespace Croicu.Desk.Tools.Service;

/// <summary>
/// Generic child-process-with-redirected-stdio primitive: spawns a process given an
/// already-resolved command/args/env/working directory and returns a <see cref="ToolProcess"/>
/// wrapping it. Deliberately has no notion of the MCP tool registry itself (see
/// <see cref="McpToolLauncher"/>, which reads a registry fragment and calls this) -- kept generic
/// so a future non-registry-sourced process, or a differently-shaped registry entry, can reuse the
/// same primitive.
/// </summary>
internal static class ToolLauncher
{
    // No BOM: same reasoning as Host.WriteEncoding/Client.WriteEncoding -- a redirected child
    // process's default StandardInputEncoding is the system's OEM/console codepage, not UTF-8, and
    // even Encoding.UTF8 would prepend a 3-byte BOM to the first thing written. A tool speaking
    // newline-delimited JSON-RPC over stdio (see docs/PROTOCOL.md's Hello entry) expects plain
    // UTF-8 text with no preamble.
    private static readonly UTF8Encoding WriteEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Spawns <paramref name="command"/> with <paramref name="args"/>/<paramref name="env"/>, both
    /// standard input and standard output redirected, and <paramref name="workingDirectory"/> as
    /// its working directory, with or without a console window (<paramref name="showConsole"/>, see
    /// <see cref="CreateStartInfo"/>). Throws <see cref="AppError"/> if the process fails to start.
    /// </summary>
    internal static ToolProcess Launch(string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, string workingDirectory, bool showConsole = false)
    {
        var startInfo = CreateStartInfo(command, args, env, workingDirectory, showConsole);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            // Thrown (not a null return) when the OS itself can't find/launch the executable, e.g.
            // 'command' isn't on PATH -- the null-return case below covers Process.Start returning
            // without an exception but no usable process object (rare, but documented as possible).
            throw new AppError($"could not start '{command}': {error.Message}");
        }

        return process is null
            ? throw new AppError($"could not start '{command}'.")
            : new ToolProcess(process);
    }

    /// <summary>
    /// Builds the process start description for a tool: stdin/stdout redirected, the registry's own env, and the
    /// parent-watchdog variable. <paramref name="showConsole"/> false (the default for every tool, issue #44) sets
    /// <see cref="ProcessStartInfo.CreateNoWindow"/>: the tools talk over redirected stdio and never need a console
    /// window, and one only makes sense while debugging a tool. Split out so a test can inspect it without
    /// starting a process.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, string workingDirectory, bool showConsole)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = WriteEncoding,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = !showConsole,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        // Set after the registry's own env so a fragment can't override it: the tool's parent
        // watchdog (see ParentWatchdog, issue #45) must watch Service itself.
        startInfo.Environment[ParentWatchdog.EnvironmentVariable] = Environment.ProcessId.ToString();

        return startInfo;
    }
}
