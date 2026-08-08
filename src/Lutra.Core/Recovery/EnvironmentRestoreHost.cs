using System.Diagnostics;
using Lutra.Core.Configuration;
using Lutra.Core.Volumes;

namespace Lutra.Core.Recovery;

public interface IEnvironmentRestoreHost
{
    bool IsPrivileged { get; }
    long GetAvailableBytes(string path);
    Task<bool> ToolExistsAsync(string tool, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetVolumeConsumersAsync(string volume, CancellationToken cancellationToken);
    Task StopContainersAsync(IReadOnlyList<string> containers, CancellationToken cancellationToken);
    Task StopSystemdUnitsAsync(IReadOnlyList<string> units, CancellationToken cancellationToken);
    Task RestoreVolumeAsync(string volume, string payloadPath, CancellationToken cancellationToken);
    Task ValidateServicesAsync(
        string rootPath,
        IReadOnlyList<string> systemdUnits,
        CancellationToken cancellationToken);
    Task ActivateServicesAsync(
        IReadOnlyList<string> systemdUnits,
        IReadOnlyList<string> containers,
        CancellationToken cancellationToken);
}

internal sealed class SystemEnvironmentRestoreHost : IEnvironmentRestoreHost
{
    public bool IsPrivileged => Environment.IsPrivilegedProcess;

    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public async Task<bool> ToolExistsAsync(string tool, CancellationToken cancellationToken)
    {
        var arguments = tool switch
        {
            "docker" => new[] { "--version" },
            "systemctl" => new[] { "--version" },
            "nginx" => new[] { "-v" },
            _ => new[] { "--version" }
        };
        return (await RunAsync(tool, arguments, allowFailure: true, cancellationToken)).ExitCode == 0;
    }

    public async Task<IReadOnlyList<string>> GetVolumeConsumersAsync(
        string volume,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "docker",
            ["ps", "--filter", $"volume={volume}", "--format", "{{.Names}}"],
            allowFailure: false,
            cancellationToken);
        return result.Output.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public async Task StopContainersAsync(
        IReadOnlyList<string> containers,
        CancellationToken cancellationToken)
    {
        foreach (var container in containers)
        {
            var exists = await RunAsync("docker", ["container", "inspect", container], allowFailure: true, cancellationToken);
            if (exists.ExitCode == 0)
                _ = await RunAsync("docker", ["stop", container], allowFailure: false, cancellationToken);
        }
    }

    public async Task StopSystemdUnitsAsync(
        IReadOnlyList<string> units,
        CancellationToken cancellationToken)
    {
        foreach (var unit in units)
        {
            var loadState = await RunAsync(
                "systemctl", ["show", unit, "--property", "LoadState", "--value"],
                allowFailure: true, cancellationToken);
            if (loadState.ExitCode == 0
                && !loadState.Output.Trim().Equals("not-found", StringComparison.Ordinal))
                _ = await RunAsync("systemctl", ["stop", unit], allowFailure: false, cancellationToken);
        }
    }

    public Task RestoreVolumeAsync(
        string volume,
        string payloadPath,
        CancellationToken cancellationToken)
        => DockerVolumeArchive.RestoreAsync(volume, payloadPath, CompressionType.Gzip, cancellationToken);

    public async Task ValidateServicesAsync(
        string rootPath,
        IReadOnlyList<string> systemdUnits,
        CancellationToken cancellationToken)
    {
        if (systemdUnits.Count > 0)
        {
            var unitPaths = systemdUnits.Select(unit =>
            {
                var staged = Path.Combine(rootPath, "etc", "systemd", "system", unit);
                return File.Exists(staged) ? staged : unit;
            }).ToArray();
            _ = await RunAsync("systemd-analyze", ["verify", .. unitPaths], allowFailure: false, cancellationToken);
        }

        if (systemdUnits.Any(unit => unit.Equals("nginx.service", StringComparison.OrdinalIgnoreCase))
            && File.Exists(Path.Combine(rootPath, "etc", "nginx", "nginx.conf"))
            && await ToolExistsAsync("nginx", cancellationToken))
        {
            _ = await RunAsync(
                "nginx",
                ["-t", "-p", EnsureTrailingSeparator(rootPath), "-c", "etc/nginx/nginx.conf"],
                allowFailure: false,
                cancellationToken);
        }
    }

    public async Task ActivateServicesAsync(
        IReadOnlyList<string> systemdUnits,
        IReadOnlyList<string> containers,
        CancellationToken cancellationToken)
    {
        if (systemdUnits.Count > 0)
        {
            _ = await RunAsync("systemctl", ["daemon-reload"], allowFailure: false, cancellationToken);
            foreach (var unit in systemdUnits)
                _ = await RunAsync("systemctl", ["enable", "--now", unit], allowFailure: false, cancellationToken);
        }
        foreach (var container in containers)
            _ = await RunAsync("docker", ["start", container], allowFailure: false, cancellationToken);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static async Task<HostCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool allowFailure,
        CancellationToken cancellationToken)
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

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("host_command_unavailable");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
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
            _ = await error;
            if (!allowFailure && process.ExitCode != 0)
                throw new InvalidOperationException("host_command_failed");
            return new HostCommandResult(process.ExitCode, await output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (allowFailure)
                return new HostCommandResult(-1, "");
            throw new InvalidOperationException("host_command_unavailable");
        }
    }

    private sealed record HostCommandResult(int ExitCode, string Output);
}
