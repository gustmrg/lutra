using System.Diagnostics;
using System.Text.Json;
using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Sync;

public sealed class RsyncService
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;
    private readonly IRsyncProcessRunner _processRunner;

    public RsyncService(
        BackupConfig config,
        IBackupHistoryService history,
        IRsyncProcessRunner? processRunner = null)
    {
        _config = config;
        _history = history;
        _processRunner = processRunner ?? new SystemRsyncProcessRunner();
    }

    public async Task<SyncValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var sync = GetConfig();
        if (!File.Exists(sync.SshKeyPath))
            return new SyncValidationResult(false, $"SSH key does not exist: {sync.SshKeyPath}");

        var localRsync = await _processRunner.RunAsync("rsync", ["--version"], cancellationToken);
        if (localRsync.ExitCode != 0)
            return new SyncValidationResult(false, "rsync is not installed locally.");

        var remoteCommand = $"command -v rsync >/dev/null && mkdir -p {ShellQuote(sync.DestinationPath)} && test -w {ShellQuote(sync.DestinationPath)}";
        var ssh = await _processRunner.RunAsync("ssh", BuildSshArguments(sync, remoteCommand), cancellationToken);
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
        string? walSource = null;
        string? walDestination = null;

        if (targetName is not null)
        {
            var target = _config.AllTargets().FirstOrDefault(item =>
                item.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new SyncResult(false, dryRun, targetName, null, "Target was not found.", startedAt);

            source = Path.Combine(source, target.Name);
            destination = CombineRemotePath(destination, target.Name);
            if (target is DatabaseTarget { Type: DatabaseType.PostgreSql, PostgresWalArchivePath: not null })
            {
                walSource = Path.Combine(_config.BackupDirectory, target.Name + "-wal");
                walDestination = CombineRemotePath(sync.DestinationPath, target.Name + "-wal");
            }
        }

        if (!Directory.Exists(source))
            return new SyncResult(false, dryRun, targetName, null, $"Local source directory does not exist: {source}", startedAt);

        var args = new List<string> { "-a", "--human-readable", "--itemize-changes" };
        if (dryRun)
            args.Add("--dry-run");
        if (delete || sync.Delete)
            args.Add("--delete");
        if (targetName is null)
            AddFullRootExclusions(args);
        args.AddRange(sync.ExtraArgs);
        args.Add("-e");
        args.Add(BuildRsyncSshCommand(sync));
        args.Add(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        args.Add($"{sync.User}@{sync.Host}:{destination.TrimEnd('/')}/");

        var process = await _processRunner.RunAsync("rsync", args, cancellationToken);
        var success = process.ExitCode == 0;
        var output = process.StdOut;
        var error = success ? null : SafeError(process);
        if (success && walSource is not null && Directory.Exists(walSource))
        {
            var walArgs = args.ToList();
            walArgs[^2] = walSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            walArgs[^1] = $"{sync.User}@{sync.Host}:{walDestination!.TrimEnd('/')}/";
            var walProcess = await _processRunner.RunAsync("rsync", walArgs, cancellationToken);
            success = walProcess.ExitCode == 0;
            output = output + walProcess.StdOut;
            if (!success)
                error = SafeError(walProcess);
        }
        var result = new SyncResult(success, dryRun, targetName, output, error, startedAt);

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
            var completedAt = DateTimeOffset.UtcNow;
            var startedAt = new DateTimeOffset(
                DateTime.SpecifyKind(result.StartedAt, DateTimeKind.Utc));
            await _history.AddRecordAsync(new HistoryRecord
            {
                TargetName = targetName,
                OperationType = HistoryOperationType.Sync,
                Status = result.Success
                    ? HistoryOperationStatus.Succeeded
                    : HistoryOperationStatus.Failed,
                StartedAt = startedAt,
                UpdatedAt = completedAt,
                CompletedAt = completedAt,
                FileSizeBytes = 0,
                DurationMs = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
                ErrorMessage = result.ErrorMessage
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

    private void AddFullRootExclusions(List<string> arguments)
    {
        string[] exclusions =
        [
            "/backup-history.json",
            "/.backup-history.lock",
            "/.locks/",
            "*.tmp",
            "/lutra.db",
            "/lutra.db-wal",
            "/lutra.db-shm"
        ];
        foreach (var exclusion in exclusions)
        {
            arguments.Add("--exclude");
            arguments.Add(exclusion);
        }

        if (!string.IsNullOrWhiteSpace(_config.StateDirectory))
        {
            var backupRoot = Path.GetFullPath(_config.BackupDirectory);
            var stateRoot = Path.GetFullPath(_config.StateDirectory);
            var relative = Path.GetRelativePath(backupRoot, stateRoot);
            if (!relative.Equals(".", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                arguments.Add("--exclude");
                arguments.Add("/" + relative.Replace(Path.DirectorySeparatorChar, '/') + "/");
            }
        }
    }

    private static string SafeError(RsyncProcessResult result)
        => string.IsNullOrWhiteSpace(result.StdErr)
            ? $"command exited with code {result.ExitCode}"
            : result.StdErr.Trim();

}

public interface IRsyncProcessRunner
{
    Task<RsyncProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed record RsyncProcessResult(int ExitCode, string StdOut, string StdErr);

internal sealed class SystemRsyncProcessRunner : IRsyncProcessRunner
{
    public async Task<RsyncProcessResult> RunAsync(
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
                return new RsyncProcessResult(-1, "", "Failed to start process.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new RsyncProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new RsyncProcessResult(-1, "", ex.Message);
        }
    }
}

public sealed record SyncResult(
    bool Success,
    bool DryRun,
    string? TargetName,
    string? Output,
    string? ErrorMessage,
    DateTime StartedAt);

public sealed record SyncValidationResult(bool Success, string Message);
