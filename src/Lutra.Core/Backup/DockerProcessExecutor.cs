using System.Diagnostics;

namespace Lutra.Core.Backup;

public class DockerProcessExecutor : IProcessExecutor
{
    public async Task<ProcessResult> ExecuteAsync(DockerExecCommand command, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("exec");

        if (command.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in command.EnvironmentVariables)
            {
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"{key}={value}");
            }
        }

        psi.ArgumentList.Add(command.ContainerName);
        psi.ArgumentList.Add(command.Command);

        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process.");

        // Stream stdout to a temp file to avoid holding large backups in memory
        var tempFile = Path.GetTempFileName();
        await using (var tempStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardOutput.BaseStream.CopyToAsync(tempStream, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stderr = await stderrTask;

            // Reopen as a readable stream with auto-delete on close
            var outputStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

            return new ProcessResult(process.ExitCode, outputStream, stderr);
        }
    }
}
