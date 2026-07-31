using System.Diagnostics;

namespace Lutra.Core.Inventory;

/// <summary>
/// Runs short-lived processes on the host and captures their output.
/// Used by inventory collectors; unlike <see cref="Backup.IProcessExecutor"/>,
/// these commands run outside of Docker.
/// </summary>
internal static class HostProcess
{
    /// <summary>
    /// Runs a command and captures stdout/stderr. Returns a result with exit code
    /// <c>-1</c> when the process cannot be started (e.g. the tool is not installed).
    /// </summary>
    public static async Task<HostProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return new HostProcessResult(-1, string.Empty, "failed to start process");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            return new HostProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new HostProcessResult(-1, string.Empty, ex.Message);
        }
    }
}

internal sealed record HostProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool IsSuccess => ExitCode == 0;
}
