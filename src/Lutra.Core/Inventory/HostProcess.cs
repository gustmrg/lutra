using System.Diagnostics;

namespace Lutra.Core.Inventory;

public interface IHostProcessRunner
{
    Task<HostProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed record HostProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool IsSuccess => ExitCode == 0;
}

internal sealed class SystemHostProcessRunner : IHostProcessRunner
{
    public async Task<HostProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new HostProcessResult(-1, "", "");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            return new HostProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new HostProcessResult(-1, "", "");
        }
    }
}
