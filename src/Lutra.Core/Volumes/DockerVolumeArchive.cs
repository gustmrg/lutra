using System.Diagnostics;
using Lutra.Core.Configuration;

namespace Lutra.Core.Volumes;

public static class DockerVolumeArchive
{
    public static Task CreateAsync(
        string volume, string outputPath, CompressionType compression, CancellationToken cancellationToken = default)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var outputName = Path.GetFileName(outputPath);
        var tarFlag = compression == CompressionType.Gzip ? "czf" : "cf";
        return RunDockerAsync([
            "run", "--rm",
            "-v", $"{volume}:/data:ro",
            "-v", $"{outputDirectory}:/out",
            "alpine", "tar", tarFlag, $"/out/{outputName}", "-C", "/data", "."
        ], cancellationToken);
    }

    public static Task RestoreAsync(
        string volume, string archivePath, CompressionType compression, CancellationToken cancellationToken = default)
    {
        var inputDirectory = Path.GetDirectoryName(archivePath)!;
        var inputName = Path.GetFileName(archivePath);
        var tarFlag = compression == CompressionType.Gzip ? "xzf" : "xf";
        return RunDockerAsync([
            "run", "--rm",
            "-v", $"{volume}:/data",
            "-v", $"{inputDirectory}:/in:ro",
            "alpine", "sh", "-c",
            $"find /data -mindepth 1 -maxdepth 1 -exec rm -rf -- {{}} + && tar {tarFlag} /in/{ShellQuote(inputName)} -C /data"
        ], cancellationToken);
    }

    private static async Task RunDockerAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Docker.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Docker volume archive command failed: {(await stderr).Trim()}");
            _ = await stdout;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Docker CLI is not installed.", ex);
        }
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
