namespace Lutra.Core.Backup;

/// <summary>
/// Executes commands inside Docker containers and captures the output.
/// </summary>
/// <remarks>
/// Implementations handle process lifecycle (start, wait, error detection).
/// The returned <see cref="ProcessResult"/> owns the output stream and must be disposed by the caller.
/// </remarks>
public interface IProcessExecutor
{
    /// <summary>
    /// Executes a command inside a Docker container and captures the output.
    /// </summary>
    /// <param name="command">
    /// The Docker exec command specification including container name, executable, arguments,
    /// and optional environment variables.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> containing the exit code, standard output stream,
    /// and captured standard error text.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Docker process fails to start.
    /// </exception>
    Task<ProcessResult> ExecuteAsync(DockerExecCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of executing a command inside a Docker container.
/// </summary>
/// <param name="ExitCode">The process exit code. Zero indicates success.</param>
/// <param name="OutputStream">
/// A readable stream containing the process standard output. The caller must dispose this stream.
/// </param>
/// <param name="StandardError">Captured standard error content for diagnostics.</param>
public record ProcessResult(
    int ExitCode,
    Stream OutputStream,
    string StandardError
) : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the process exited successfully (exit code 0).
    /// </summary>
    public bool IsSuccess => ExitCode == 0;

    public void Dispose()
    {
        OutputStream.Dispose();
        GC.SuppressFinalize(this);
    }
}
