using System.ComponentModel;
using System.Diagnostics;

namespace Lutra.Core.Processes;

/// <summary>An external command to run.</summary>
/// <param name="FileName">Executable name or path.</param>
/// <param name="Arguments">Arguments, passed without shell interpretation.</param>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Variables added to the child's environment. Secrets belong here rather than
    /// in <see cref="Arguments"/>, which any local user can read from the process list.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Stream copied to the child's standard input, which is then closed.</summary>
    public Stream? Input { get; init; }

    /// <summary>
    /// Stream that receives the child's standard output. When unset, standard output
    /// is captured as text in <see cref="ProcessOutcome.StandardOutput"/>.
    /// </summary>
    public Stream? Output { get; init; }
}

public sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a command to completion. A command that cannot be started is reported
    /// as exit code <c>-1</c> with the reason in standard error. Cancellation kills
    /// the child and its descendants before the exception propagates.
    /// </summary>
    Task<ProcessOutcome> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessOutcome> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardInput = request.Input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[name] = value;

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new ProcessOutcome(-1, "", $"Could not start '{request.FileName}': {ex.Message}");
        }

        if (process is null)
            return new ProcessOutcome(-1, "", $"Could not start '{request.FileName}'.");

        using (process)
        {
            try
            {
                var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
                var standardOutput = request.Output is null
                    ? process.StandardOutput.ReadToEndAsync(cancellationToken)
                    : CopyOutputAsync(process, request.Output, cancellationToken);
                var pumps = Task.WhenAll(standardOutput, standardError);
                var exit = process.WaitForExitAsync(cancellationToken);

                if (request.Input is not null)
                    await WriteInputAsync(process, request.Input, cancellationToken);

                // A child blocked on a full pipe never exits, so a failed pump (for
                // example a full disk) has to surface before the exit is awaited.
                await await Task.WhenAny(pumps, exit);
                await exit;
                await pumps;

                return new ProcessOutcome(process.ExitCode, await standardOutput, await standardError);
            }
            finally
            {
                Kill(process);
            }
        }
    }

    private static async Task<string> CopyOutputAsync(
        Process process,
        Stream output,
        CancellationToken cancellationToken)
    {
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        return "";
    }

    private static async Task WriteInputAsync(Process process, Stream input, CancellationToken cancellationToken)
    {
        try
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited before consuming all input (for example on an
            // authentication failure). Its exit code and stderr report the cause.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }
}
