using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Files;
using Lutra.Core.History;
using Lutra.Core.Inventory;
using Lutra.Core.Volumes;

namespace Lutra.Core.Recovery;

public interface IEnvironmentVolumeArchiver
{
    Task CreateAsync(string volume, string outputPath, CancellationToken cancellationToken);
}

public interface IEnvironmentArchiveWriter
{
    Task WriteAsync(EnvironmentArchiveWriteRequest request, CancellationToken cancellationToken);
}

public sealed record EnvironmentArchiveWriteRequest(
    string OutputPath,
    EnvironmentRecoveryManifest Manifest,
    IReadOnlyDictionary<string, string> PayloadFiles,
    string InventoryJson,
    string InventoryMarkdown,
    string BackupReportJson,
    string MissingSecretsMarkdown,
    string RestoreMarkdown);

public sealed class EnvironmentBackupService
{
    public const string HistoryTargetName = "@environment";
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(24);
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;
    private readonly IInventoryCollector _inventory;
    private readonly IEnvironmentVolumeArchiver _volumeArchiver;
    private readonly IEnvironmentArchiveWriter _archiveWriter;
    private readonly Func<string, string, string, FileStream> _lockFactory;
    private readonly Action<string, string> _promoteFile;
    private readonly TimeProvider _timeProvider;

    public EnvironmentBackupService(
        BackupConfig config,
        IBackupHistoryService history,
        IInventoryCollector? inventory = null,
        IEnvironmentVolumeArchiver? volumeArchiver = null,
        IEnvironmentArchiveWriter? archiveWriter = null,
        Func<string, string, string, FileStream>? lockFactory = null,
        Action<string, string>? promoteFile = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _history = history;
        _inventory = inventory ?? new InventoryService(config);
        _volumeArchiver = volumeArchiver ?? new DockerEnvironmentVolumeArchiver();
        _archiveWriter = archiveWriter ?? new SystemEnvironmentArchiveWriter();
        _lockFactory = lockFactory ?? TargetLock.Acquire;
        _promoteFile = promoteFile ?? ((source, destination) =>
            File.Move(source, destination, overwrite: false));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EnvironmentBackupResult> BackupAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();
        var environment = _config.Environment;
        if (environment is not { Enabled: true })
            return Failed(startedAt, stopwatch.Elapsed, "Environment recovery is not enabled.");

        var artifactId = Guid.NewGuid().ToString("N")[..12];
        var operationId = $"{startedAt:yyyyMMddHHmmss}-{artifactId}";
        var stage = "initialization";
        var environmentDirectory = Path.Combine(_config.BackupDirectory, "environment");
        var stagingDirectory = Path.Combine(environmentDirectory, $".staging-{operationId}");
        var fileName = $"environment_{startedAt:yyyy-MM-dd_HHmmss}_{artifactId}.tar.gz";
        var finalPath = Path.Combine(environmentDirectory, fileName);
        var finalChecksum = finalPath + ".sha256";
        var finalDescriptor = finalPath + ".json";
        var artifactPublished = false;
        var promotedSidecars = new List<string>();
        HistoryOperationScope? operation = null;
        EnvironmentOperationLog? log = null;

        try
        {
            log = await EnvironmentOperationLog.CreateAsync(_config, operationId, startedAt, CancellationToken.None);
            await TryLogAsync(log, stage, "started", null, stopwatch.Elapsed, cancellationToken);
            operation = await HistoryOperationScope.BeginAsync(
                _history, HistoryTargetName, HistoryOperationType.Backup, cancellationToken);

            stage = "lock";
            await using var environmentLock = _lockFactory(
                _config.BackupDirectory, HistoryTargetName, "Environment backup");

            CreatePrivateDirectory(environmentDirectory);
            CleanupStaleStaging(environmentDirectory, stagingDirectory);
            CreatePrivateDirectory(stagingDirectory);
            var payloadDirectory = Path.Combine(stagingDirectory, "payloads");
            CreatePrivateDirectory(payloadDirectory);

            stage = "sources";
            var targets = ResolveTargets(environment);
            ValidateSourceBoundaries(targets);
            var payloadFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifestSources = new List<EnvironmentRecoverySource>();
            var reportSources = new List<EnvironmentBackupReportSource>();
            var restoreOrder = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payloadPath = Path.Combine(payloadDirectory, target.Name + ".tar.gz");
                List<string> excluded = [];
                EnvironmentRecoverySourceKind kind;
                switch (target)
                {
                    case FileTarget fileTarget:
                        kind = EnvironmentRecoverySourceKind.File;
                        var patterns = (fileTarget.Exclude ?? [])
                            .Concat(environment.Exclude)
                            .Concat(EnvironmentBackupConfig.MandatorySecretExcludes)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        excluded = (await FileArchive.CreateWithReportAsync(
                            fileTarget.Paths, patterns, payloadPath, CompressionType.Gzip, cancellationToken)).ToList();
                        break;
                    case VolumeTarget volumeTarget:
                        kind = EnvironmentRecoverySourceKind.Volume;
                        await _volumeArchiver.CreateAsync(volumeTarget.Volume, payloadPath, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("unsupported_source");
                }

                SetPrivateFileMode(payloadPath);
                var payloadInfo = new FileInfo(payloadPath);
                var payloadSha = await BackupIntegrity.ComputeSha256Async(payloadPath, cancellationToken);
                payloadFiles[target.Name] = payloadPath;
                manifestSources.Add(new EnvironmentRecoverySource
                {
                    Name = target.Name,
                    Kind = kind,
                    PayloadPath = $"payload/{(kind == EnvironmentRecoverySourceKind.File ? "files" : "volumes")}/{target.Name}.tar.gz",
                    SizeBytes = payloadInfo.Length,
                    Sha256 = payloadSha,
                    RestoreOrder = restoreOrder++
                });
                reportSources.Add(new EnvironmentBackupReportSource
                {
                    Name = target.Name,
                    Kind = kind,
                    ExcludedEntries = excluded.Order(StringComparer.Ordinal).ToList()
                });
            }

            stage = "inventory";
            var hasVolume = targets.Any(target => target is VolumeTarget);
            var inventoryPolicy = new InventoryCollectionPolicy(
                RequirePackages: true,
                RequireDocker: hasVolume || environment.DockerContainers.Count > 0,
                RequireSystemd: environment.SystemdUnits.Count > 0,
                OptionalCollectors: (_config.Inventory?.Collectors ?? [])
                    .Where(name => name is "crontabs" or "firewall")
                    .ToList());
            var snapshot = await _inventory.CollectSnapshotAsync(inventoryPolicy, cancellationToken);
            if (snapshot.HasRequiredFailures || !HasCompleteInventory(snapshot))
                throw new InvalidOperationException("required_collector_failed");

            stage = "archive";
            manifestSources = manifestSources.OrderBy(source => source.Name, StringComparer.Ordinal).ToList();
            var manifest = new EnvironmentRecoveryManifest
            {
                FormatVersion = EnvironmentRecoveryManifest.CurrentFormatVersion,
                ArtifactId = artifactId,
                CreatedAt = startedAt,
                LutraVersion = GetVersion(),
                Sources = manifestSources,
                RequiredTools = BuildRequiredTools(hasVolume, environment),
                SystemdUnits = environment.SystemdUnits.Order(StringComparer.Ordinal).ToList(),
                DockerContainers = environment.DockerContainers.Order(StringComparer.Ordinal).ToList()
            };
            var report = new EnvironmentBackupReport
            {
                ArtifactId = artifactId,
                StartedAt = startedAt,
                CompletedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Success = true,
                Sources = reportSources.OrderBy(source => source.Name, StringComparer.Ordinal).ToList(),
                Warnings = hasVolume
                    ? ["Docker volume payloads are crash-consistent only unless applications are stopped externally."]
                    : []
            };
            var stagingArchive = Path.Combine(stagingDirectory, fileName);
            await _archiveWriter.WriteAsync(new EnvironmentArchiveWriteRequest(
                stagingArchive,
                manifest,
                payloadFiles,
                InventoryRenderer.ToJson(snapshot),
                InventoryRenderer.ToMarkdown(snapshot),
                EnvironmentRecoveryArchive.Serialize(report),
                BuildMissingSecrets(environment),
                BuildRestoreInstructions(manifest)), cancellationToken);
            await EnvironmentRecoveryArchive.ValidateAsync(stagingArchive, cancellationToken);

            stage = "publication";
            var archiveInfo = new FileInfo(stagingArchive);
            var sha256 = await BackupIntegrity.ComputeSha256Async(stagingArchive, cancellationToken);
            var descriptor = new EnvironmentRecoveryDescriptor
            {
                FormatVersion = manifest.FormatVersion,
                ArtifactId = artifactId,
                CreatedAt = startedAt,
                ArtifactFileName = fileName,
                FileSizeBytes = archiveInfo.Length,
                Sha256 = sha256,
                LutraVersion = manifest.LutraVersion,
                Sources = manifest.Sources.Select(source => new EnvironmentRecoveryDescriptorSource
                {
                    Name = source.Name,
                    Kind = source.Kind
                }).ToList(),
                Success = true
            };
            var stagingChecksum = stagingArchive + ".sha256";
            var stagingDescriptor = stagingArchive + ".json";
            await WritePrivateTextAsync(stagingChecksum, $"{sha256}  {fileName}\n", cancellationToken);
            await WritePrivateTextAsync(
                stagingDescriptor, EnvironmentRecoveryArchive.Serialize(descriptor), cancellationToken);

            _promoteFile(stagingChecksum, finalChecksum);
            promotedSidecars.Add(finalChecksum);
            _promoteFile(stagingDescriptor, finalDescriptor);
            promotedSidecars.Add(finalDescriptor);
            _promoteFile(stagingArchive, finalPath);
            artifactPublished = true;

            stage = "history";
            stopwatch.Stop();
            await operation.CompleteAsync(new HistoryOperationCompletion(
                FileName: fileName,
                FileSizeBytes: archiveInfo.Length,
                Sha256: sha256,
                ManifestFileName: Path.GetFileName(finalDescriptor),
                DurationMs: (long)stopwatch.Elapsed.TotalMilliseconds));

            try
            {
                stage = "retention";
                await ApplyRetentionAsync(environment.Retention ?? _config.Retention, cancellationToken);
            }
            catch
            {
                await TryLogAsync(log, stage, "warning", "retention_failed", stopwatch.Elapsed, cancellationToken);
            }

            await TryLogAsync(log, "complete", "succeeded", null, stopwatch.Elapsed, cancellationToken);
            return new EnvironmentBackupResult(
                true, finalPath, archiveInfo.Length, sha256, null, startedAt, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await TryTransitionAsync(operation, cancelled: true, "operation_cancelled", stopwatch.Elapsed);
            if (log is not null)
                await TryLogAsync(log, stage, "cancelled", "operation_cancelled", stopwatch.Elapsed, CancellationToken.None);
            CleanupFailedPublication(artifactPublished, finalPath, promotedSidecars);
            return Failed(startedAt, stopwatch.Elapsed, "Environment backup was cancelled.");
        }
        catch
        {
            stopwatch.Stop();
            var category = ErrorCategory(stage);
            await TryTransitionAsync(operation, cancelled: false, category, stopwatch.Elapsed);
            if (log is not null)
                await TryLogAsync(log, stage, "failed", category, stopwatch.Elapsed, CancellationToken.None);
            CleanupFailedPublication(artifactPublished, finalPath, promotedSidecars);
            return Failed(startedAt, stopwatch.Elapsed, $"Environment backup failed during {SafeStage(stage)}.");
        }
        finally
        {
            if (operation is not null)
                await operation.DisposeAsync();
            if (log is not null)
            {
                try
                {
                    await log.DisposeAsync();
                }
                catch
                {
                }
            }
            DeleteDirectory(stagingDirectory);
        }
    }

    private List<IBackupTarget> ResolveTargets(EnvironmentBackupConfig environment)
    {
        return environment.Targets.Select(name => _config.AllTargets().Single(target =>
                target.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(target => target.Name, StringComparer.Ordinal)
            .ToList();
    }

    private void ValidateSourceBoundaries(IEnumerable<IBackupTarget> targets)
    {
        var protectedPaths = new[] { _config.BackupDirectory, _config.StateDirectory! }
            .Select(ResolvePhysicalPath)
            .ToList();
        foreach (var path in targets.OfType<FileTarget>().SelectMany(target => target.Paths))
        {
            var source = ResolvePhysicalPath(path);
            if (protectedPaths.Any(protectedPath => PathsOverlap(source, protectedPath)))
                throw new InvalidOperationException("source_overlaps_lutra_storage");
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (entry?.LinkTarget is not null)
                current = entry.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return Path.GetFullPath(current);
    }

    private static bool PathsOverlap(string first, string second)
        => IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private async Task ApplyRetentionAsync(RetentionPolicy retention, CancellationToken cancellationToken)
    {
        var records = (await _history.GetRecordsByTargetAsync(HistoryTargetName, cancellationToken))
            .Where(record => record.OperationType == HistoryOperationType.Backup
                             && record.Status == HistoryOperationStatus.Succeeded
                             && !string.IsNullOrWhiteSpace(record.FileName))
            .OrderByDescending(record => record.StartedAt)
            .ToList();
        var cutoff = _timeProvider.GetUtcNow().AddDays(-retention.MaxAgeDays);
        var candidates = records.Select((record, index) => new
            {
                Record = record,
                Index = index,
                CountExceeded = index >= retention.MaxCount,
                AgeExceeded = record.StartedAt < cutoff
            })
            .Where(item => item.Index >= retention.KeepAtLeast)
            .Where(item => retention.Mode == RetentionMode.Both
                ? item.CountExceeded && item.AgeExceeded
                : item.CountExceeded || item.AgeExceeded);

        foreach (var candidate in candidates)
        {
            var artifact = Path.Combine(_config.BackupDirectory, "environment", candidate.Record.FileName!);
            var checksum = artifact + ".sha256";
            var descriptor = artifact + ".json";
            if (!File.Exists(artifact) || !File.Exists(checksum) || !File.Exists(descriptor))
                continue;
            File.Delete(artifact);
            File.Delete(checksum);
            File.Delete(descriptor);
            await _history.RemoveRecordAsync(candidate.Record.Id, cancellationToken);
        }
    }

    private void CleanupStaleStaging(string environmentDirectory, string currentStaging)
    {
        var cutoff = _timeProvider.GetUtcNow() - StaleStagingAge;
        foreach (var directory in Directory.GetDirectories(environmentDirectory, ".staging-*"))
        {
            if (directory != currentStaging && Directory.GetLastWriteTimeUtc(directory) < cutoff.UtcDateTime)
                DeleteDirectory(directory);
        }
        foreach (var file in Directory.GetFiles(environmentDirectory, ".*.tmp"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                File.Delete(file);
        }
        foreach (var sidecar in Directory.GetFiles(environmentDirectory, "environment_*.tar.gz.*"))
        {
            if (Path.GetExtension(sidecar) is not (".sha256" or ".json"))
                continue;
            var artifact = Path.ChangeExtension(sidecar, null);
            if (!File.Exists(artifact) && File.GetLastWriteTimeUtc(sidecar) < cutoff.UtcDateTime)
                DeleteIfExists(sidecar);
        }
    }

    private static bool HasCompleteInventory(InventorySnapshot snapshot)
    {
        var names = snapshot.Sections.Select(section => section.Name).ToHashSet(StringComparer.Ordinal);
        return names.Contains("os")
               && names.Contains("packages")
               && names.Contains("docker")
               && names.Contains("systemd");
    }

    private static List<string> BuildRequiredTools(bool hasVolume, EnvironmentBackupConfig environment)
    {
        var tools = new List<string>();
        if (hasVolume || environment.DockerContainers.Count > 0)
            tools.Add("docker");
        if (environment.SystemdUnits.Count > 0)
            tools.Add("systemctl");
        return tools.Order(StringComparer.Ordinal).ToList();
    }

    private static string BuildMissingSecrets(EnvironmentBackupConfig environment)
    {
        var output = new StringBuilder();
        output.AppendLine("# Missing Secrets");
        output.AppendLine();
        output.AppendLine("This plaintext recovery set intentionally excludes secret values.");
        output.AppendLine("Restore them from your external secret service or manual recovery records before starting applications.");
        output.AppendLine("Docker volume contents cannot be classified; selected volumes may contain secret values.");
        output.AppendLine();
        output.AppendLine("Built-in excluded patterns:");
        foreach (var pattern in EnvironmentBackupConfig.MandatorySecretExcludes)
            output.AppendLine($"- `{pattern}`");
        foreach (var pattern in environment.Exclude.Order(StringComparer.Ordinal))
            output.AppendLine($"- `{pattern}` (configured)");
        return output.ToString();
    }

    private static string BuildRestoreInstructions(EnvironmentRecoveryManifest manifest)
    {
        var output = new StringBuilder();
        output.AppendLine("# Environment Recovery");
        output.AppendLine();
        output.AppendLine("This set is plaintext. Restrict access before inspecting or restoring it.");
        output.AppendLine("Run `lutra environment restore` without `--apply` first when restore support is available.");
        output.AppendLine();
        output.AppendLine("Restore order:");
        foreach (var source in manifest.Sources.OrderBy(source => source.RestoreOrder))
            output.AppendLine($"{source.RestoreOrder + 1}. `{source.Name}` ({source.Kind.ToString().ToLowerInvariant()})");
        output.AppendLine();
        output.AppendLine("Restore external secrets, validate services, and activate them only after payload restoration.");
        return output.ToString();
    }

    private static async Task TryTransitionAsync(
        HistoryOperationScope? operation,
        bool cancelled,
        string category,
        TimeSpan duration)
    {
        if (operation is null)
            return;
        try
        {
            if (cancelled)
                await operation.CancelAsync(category, (long)duration.TotalMilliseconds);
            else
                await operation.FailAsync(category, (long)duration.TotalMilliseconds);
        }
        catch
        {
        }
    }

    private static async Task TryLogAsync(
        EnvironmentOperationLog log,
        string stage,
        string status,
        string? errorCategory,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await log.WriteAsync(stage, status, errorCategory, duration, cancellationToken);
        }
        catch
        {
        }
    }

    private static void CleanupFailedPublication(
        bool artifactPublished,
        string finalPath,
        IEnumerable<string> promotedSidecars)
    {
        if (artifactPublished)
            return;
        DeleteIfExists(finalPath);
        foreach (var sidecar in promotedSidecars)
            DeleteIfExists(sidecar);
    }

    private static string ErrorCategory(string stage) => stage switch
    {
        "lock" => "lock_unavailable",
        "sources" => "source_capture_failed",
        "inventory" => "inventory_failed",
        "archive" => "archive_failed",
        "publication" => "publication_failed",
        "history" => "history_failed",
        _ => "environment_backup_failed"
    };

    private static string SafeStage(string stage) => stage switch
    {
        "lock" => "lock acquisition",
        "sources" => "source capture",
        "inventory" => "inventory collection",
        "archive" => "archive creation",
        "publication" => "artifact publication",
        "history" => "history finalization",
        _ => "initialization"
    };

    private static EnvironmentBackupResult Failed(DateTime startedAt, TimeSpan duration, string message)
        => new(false, null, null, null, message, startedAt, duration);

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static async Task WritePrivateTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = CreatePrivateFile(path);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = PrivateFileMode
            });
        }
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, PrivateFileMode);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string GetVersion()
        => typeof(EnvironmentBackupService).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(EnvironmentBackupService).Assembly.GetName().Version?.ToString()
           ?? "unknown";
}

internal sealed class DockerEnvironmentVolumeArchiver : IEnvironmentVolumeArchiver
{
    public Task CreateAsync(string volume, string outputPath, CancellationToken cancellationToken)
        => DockerVolumeArchive.CreateAsync(volume, outputPath, CompressionType.Gzip, cancellationToken);
}

internal sealed class SystemEnvironmentArchiveWriter : IEnvironmentArchiveWriter
{
    public Task WriteAsync(EnvironmentArchiveWriteRequest request, CancellationToken cancellationToken)
        => EnvironmentRecoveryArchive.WriteAsync(
            request.OutputPath,
            request.Manifest,
            request.PayloadFiles,
            request.InventoryJson,
            request.InventoryMarkdown,
            request.BackupReportJson,
            request.MissingSecretsMarkdown,
            request.RestoreMarkdown,
            cancellationToken);
}

internal sealed class EnvironmentOperationLog : IAsyncDisposable
{
    private const UnixFileMode LogDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode LogFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly FileStream _stream;
    private readonly DateTime _startedAt;

    private EnvironmentOperationLog(FileStream stream, DateTime startedAt)
    {
        _stream = stream;
        _startedAt = startedAt;
    }

    public static Task<EnvironmentOperationLog> CreateAsync(
        BackupConfig config,
        string operationId,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(config.StateDirectory!, "logs", "environment");
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(directory, LogDirectoryMode);
            File.SetUnixFileMode(directory, LogDirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
        var path = Path.Combine(directory, operationId + ".jsonl");
        var stream = OperatingSystem.IsLinux()
            ? new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = LogFileMode
            })
            : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
        return Task.FromResult(new EnvironmentOperationLog(stream, startedAt));
    }

    public async Task WriteAsync(
        string stage,
        string status,
        string? errorCategory,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var entry = new
        {
            timestamp = _startedAt + elapsed,
            stage,
            status,
            error_category = errorCategory,
            duration_ms = (long)elapsed.TotalMilliseconds
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
