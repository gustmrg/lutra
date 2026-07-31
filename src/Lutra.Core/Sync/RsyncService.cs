using System.Diagnostics;
using System.Text.Json;
using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Sync;

public sealed class RsyncService
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;

    public RsyncService(BackupConfig config, IBackupHistoryService history)
    {
        _config = config;
        _history = history;
    }

    public async Task<SyncValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var sync = GetConfig();
        if (!File.Exists(sync.SshKeyPath))
            return new SyncValidationResult(false, $"SSH key does not exist: {sync.SshKeyPath}");

        var localRsync = await RunAsync("rsync", ["--version"], cancellationToken);
        if (localRsync.ExitCode != 0)
            return new SyncValidationResult(false, "rsync is not installed locally.");

        var remoteCommand = $"command -v rsync >/dev/null && mkdir -p {ShellQuote(sync.DestinationPath)} && test -w {ShellQuote(sync.DestinationPath)}";
        var ssh = await RunAsync("ssh", BuildSshArguments(sync, remoteCommand), cancellationToken);
        return ssh.ExitCode == 0
            ? new SyncValidationResult(true, "SSH connectivity, remote rsync, and destination write access are available.")
            : new SyncValidationResult(false, $"Remote validation failed: {SafeError(ssh)}");
    }

    public async Task<SyncResult> SyncAsync(
        string? targetName,
        bool dryRun,
        bool delete,
        CancellationToken cancellationToken = default)
    {
        var sync = GetConfig();
        var startedAt = DateTime.UtcNow;
        var source = _config.BackupDirectory;
        var destination = sync.DestinationPath;

        if (targetName is not null)
        {
            var target = _config.AllTargets().FirstOrDefault(item =>
                item.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new SyncResult(false, dryRun, targetName, null, "Target was not found.", startedAt);

            source = Path.Combine(source, target.Name);
            destination = CombineRemotePath(destination, target.Name);
        }

        if (!Directory.Exists(source))
            return new SyncResult(false, dryRun, targetName, null, $"Local source directory does not exist: {source}", startedAt);

        var args = new List<string> { "-a", "--human-readable", "--itemize-changes" };
        if (dryRun)
            args.Add("--dry-run");
        if (delete || sync.Delete)
            args.Add("--delete");
        args.AddRange(sync.ExtraArgs);
        args.Add("-e");
        args.Add(BuildRsyncSshCommand(sync));
        args.Add(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        args.Add($"{sync.User}@{sync.Host}:{destination.TrimEnd('/')}/");

        var process = await RunAsync("rsync", args, cancellationToken);
        var result = new SyncResult(
            process.ExitCode == 0,
            dryRun,
            targetName,
            process.StdOut,
            process.ExitCode == 0 ? null : SafeError(process),
            startedAt);

        if (!dryRun)
        {
            await WriteMarkerAsync(result, cancellationToken);
            await RecordSyncAsync(result, cancellationToken);
        }
        return result;
    }

    private async Task RecordSyncAsync(SyncResult result, CancellationToken cancellationToken)
    {
        IEnumerable<string> targetNames = result.TargetName is null
            ? _config.AllTargets().Select(target => target.Name)
            : new[] { result.TargetName };
        foreach (var targetName in targetNames)
        {
            await _history.AddRecordAsync(new BackupRecord
            {
                TargetName = targetName,
                Timestamp = result.StartedAt,
                FileName = string.Empty,
                FileSizeBytes = 0,
                DurationMs = 0,
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                RecordType = "sync"
            }, cancellationToken);
        }
    }

    private async Task WriteMarkerAsync(SyncResult result, CancellationToken cancellationToken)
    {
        var markerDirectory = result.TargetName is null
            ? _config.BackupDirectory
            : Path.Combine(_config.BackupDirectory, result.TargetName);
        if (!Directory.Exists(markerDirectory))
            return;

        var marker = Path.Combine(markerDirectory, ".last-sync.json");
        var temp = marker + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(temp, marker, overwrite: true);
    }

    private RsyncConfig GetConfig()
        => _config.Sync ?? throw new ConfigurationException("An rsync 'sync' section is not configured.");

    private static IReadOnlyList<string> BuildSshArguments(RsyncConfig config, string remoteCommand)
        => ["-i", config.SshKeyPath, "-p", config.Port.ToString(), "-o", "BatchMode=yes", $"{config.User}@{config.Host}", remoteCommand];

    private static string BuildRsyncSshCommand(RsyncConfig config)
        => $"ssh -i {ShellQuote(config.SshKeyPath)} -p {config.Port} -o BatchMode=yes";

    private static string CombineRemotePath(string parent, string child)
        => parent.TrimEnd('/') + "/" + child;

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string SafeError(ProcessCapture result)
        => string.IsNullOrWhiteSpace(result.StdErr)
            ? $"command exited with code {result.ExitCode}"
            : result.StdErr.Trim();

    private static async Task<ProcessCapture> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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
                return new ProcessCapture(-1, "", "Failed to start process.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessCapture(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcessCapture(-1, "", ex.Message);
        }
    }

    private sealed record ProcessCapture(int ExitCode, string StdOut, string StdErr);
}

public sealed record SyncResult(
    bool Success,
    bool DryRun,
    string? TargetName,
    string? Output,
    string? ErrorMessage,
    DateTime StartedAt);

public sealed record SyncValidationResult(bool Success, string Message);
