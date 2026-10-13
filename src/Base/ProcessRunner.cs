using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Croicu.Desk.Tools.Base;

/// <summary>
/// Real <see cref="IProcessRunner"/>. stdout is read as raw bytes into pinned buffers that are
/// zeroed as they're outgrown or finished with, never through a <see cref="StreamReader"/> (which
/// would leave anything sensitive in strings and char buffers no one can wipe). This is best-effort
/// hygiene, not a boundary: for the secrets use case (src/Secrets) the real protection is that a
/// medium-integrity process can't read an elevated process's memory at all. Cross-platform
/// (Architecture convention 11): nothing here is Windows-only.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const string Category = "process";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private const int DefaultInitialBufferSize = 4096;

    private readonly int _initialBufferSize;

    public ProcessRunner()
        : this(DefaultInitialBufferSize)
    {
    }

    /// <summary>
    /// <paramref name="initialBufferSize"/> is a testing seam: a tiny value forces the
    /// grow-and-zero-the-old-buffer path with any small output, instead of needing a program that
    /// happens to print more than the 4 KiB default.
    /// </summary>
    internal ProcessRunner(int initialBufferSize)
    {
        _initialBufferSize = initialBufferSize;
    }

    public ProcessResult Run(string fileName, IReadOnlyList<string> args, ReadOnlySpan<byte> stdin)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Win32Exception error)
        {
            throw new AppError($"could not start '{fileName}': {error.Message}", Category);
        }

        if (started is null)
        {
            throw new AppError($"could not start '{fileName}'.", Category);
        }

        using var process = started;

        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, _initialBufferSize);
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            var input = process.StandardInput.BaseStream;
            input.Write(stdin);
            input.Flush();
            input.Close();
        }
        catch (IOException)
        {
            // The program exited before reading all of stdin; its exit code and stderr still say why.
        }

        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new AppError($"'{fileName}' did not finish within {RunTimeout.TotalSeconds:F0}s; killed it.", Category);
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, int initialBufferSize)
    {
        var buffer = GC.AllocateArray<byte>(initialBufferSize, pinned: true);
        var length = 0;

        while (true)
        {
            if (length == buffer.Length)
            {
                var larger = GC.AllocateArray<byte>(buffer.Length * 2, pinned: true);
                buffer.AsSpan(0, length).CopyTo(larger);
                CryptographicOperations.ZeroMemory(buffer);
                buffer = larger;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        var result = GC.AllocateArray<byte>(length, pinned: true);
        buffer.AsSpan(0, length).CopyTo(result);
        CryptographicOperations.ZeroMemory(buffer);
        return result;
    }
}
