using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Inventory;

namespace Lutra.Core.Recovery;

public sealed class EnvironmentRestoreService
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly BackupConfig _config;
    private readonly IEnvironmentRestoreHost _host;
    private readonly TimeProvider _timeProvider;

    public EnvironmentRestoreService(
        BackupConfig config,
        IEnvironmentRestoreHost? host = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _host = host ?? new SystemEnvironmentRestoreHost();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EnvironmentInspectResult> InspectAsync(
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(artifactPath);
        var checksumValid = false;
        try
        {
            checksumValid = (await BackupIntegrity.VerifyFileAsync(fullPath, cancellationToken)).Success;
            if (!checksumValid)
                throw new EnvironmentRestoreException("checksum_invalid", "Recovery set checksum verification failed.");
            var (descriptor, manifest, inventory) = await ValidateSetAsync(fullPath, cancellationToken);
            return new EnvironmentInspectResult
            {
                Success = true,
                ArtifactPath = fullPath,
                ChecksumValid = true,
                Descriptor = descriptor,
                Manifest = manifest,
                Inventory = inventory
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EnvironmentInspectResult
            {
                Success = false,
                ArtifactPath = fullPath,
                ChecksumValid = checksumValid,
                ErrorCategory = Categorize(ex),
                ErrorMessage = SafeMessage(ex)
            };
        }
    }

    public async Task<EnvironmentRestoreResult> RestoreAsync(
        string artifactPath,
        EnvironmentRestoreOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var operationId = $"restore-{startedAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36];
        var stagingDirectory = Path.Combine(_config.StateDirectory!, "environment-restore", $".staging-{operationId}");
        EnvironmentOperationLog? log = null;
        PreparedRecovery? prepared = null;
        string? rollbackDirectory = null;
        string? resumeReport = null;
        var stage = "preflight";
        var applyStarted = false;

        try
        {
            log = await EnvironmentOperationLog.CreateAsync(
                _config, operationId, startedAt, CancellationToken.None);
            await LogAsync(log, stage, "started", null, stopwatch.Elapsed, cancellationToken);
            CreatePrivateDirectory(stagingDirectory);
            prepared = await PrepareAsync(artifactPath, options, stagingDirectory, cancellationToken);
            await LogAsync(log, stage, "succeeded", null, stopwatch.Elapsed, cancellationToken);

            if (!options.Apply)
            {
                foreach (var action in prepared.Plan.Actions)
                    await LogActionAsync(log, action, stopwatch.Elapsed, cancellationToken);
                await LogAsync(log, "summary", "succeeded", null, stopwatch.Elapsed, cancellationToken);
                return Result(true, false, false, prepared.Plan, null, null, null, null, stopwatch.Elapsed);
            }
            if (options.ExpectedPlanToken is not null
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(options.ExpectedPlanToken),
                    Encoding.UTF8.GetBytes(prepared.Plan.ConfirmationToken)))
                throw new EnvironmentRestoreException("plan_changed", "Recovery plan changed after confirmation.");
            if (!prepared.Plan.CanApply)
                throw new EnvironmentRestoreException("preflight_failed", "Recovery preflight did not pass.");

            stage = "apply";
            await using var restoreLock = Backup.TargetLock.Acquire(
                _config.BackupDirectory, EnvironmentBackupService.HistoryTargetName, "Environment restore");
            applyStarted = true;
            foreach (var skipped in prepared.Plan.Actions.Where(action => action.State == EnvironmentRestoreActionState.Skipped))
                await LogActionAsync(log, skipped, stopwatch.Elapsed, cancellationToken);
            if (options.CreateRollbackCopy)
            {
                rollbackDirectory = Path.Combine(
                    _config.StateDirectory!, "rollbacks", "environment", operationId);
                CreatePrivateDirectory(rollbackDirectory);
            }

            if (Path.GetFullPath(options.RootPath) == Path.GetPathRoot(Path.GetFullPath(options.RootPath)))
                await _host.StopSystemdUnitsAsync(prepared.Manifest.SystemdUnits, cancellationToken);

            var containersToStop = prepared.Plan.Actions
                .OfType<EnvironmentVolumeRestoreAction>()
                .Where(action => action.State != EnvironmentRestoreActionState.Skipped)
                .SelectMany(action => action.Consumers)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (prepared.Plan.RootPath == Path.GetPathRoot(prepared.Plan.RootPath))
                containersToStop = containersToStop.Concat(prepared.Manifest.DockerContainers)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            if (containersToStop.Count > 0)
                await _host.StopContainersAsync(containersToStop, cancellationToken);

            foreach (var source in prepared.Manifest.Sources.OrderBy(source => source.RestoreOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Kind == EnvironmentRecoverySourceKind.File)
                {
                    await ApplyFilePayloadAsync(
                        source,
                        prepared.PayloadPaths[source.Name],
                        prepared.Plan,
                        rollbackDirectory,
                        log,
                        stopwatch,
                        cancellationToken);
                }
                else if (options.IncludeVolumes)
                {
                    var target = ResolveVolumeTarget(source.Name);
                    var volumeAction = FindVolumeAction(prepared.Plan, source.Name);
                    try
                    {
                        await _host.RestoreVolumeAsync(
                            target.Volume, prepared.PayloadPaths[source.Name], cancellationToken);
                        volumeAction.State = EnvironmentRestoreActionState.Changed;
                        await LogActionAsync(log, volumeAction, stopwatch.Elapsed, cancellationToken);
                    }
                    catch
                    {
                        volumeAction.State = EnvironmentRestoreActionState.Failed;
                        await LogActionAsync(log, volumeAction, stopwatch.Elapsed, CancellationToken.None);
                        throw;
                    }
                }
            }

            if (options.ActivateServices)
            {
                stage = "validation";
                try
                {
                    await _host.ValidateServicesAsync(
                        prepared.Plan.RootPath, prepared.Manifest.SystemdUnits, cancellationToken);
                }
                catch
                {
                    foreach (var action in prepared.Plan.Actions.OfType<EnvironmentServiceRestoreAction>())
                    {
                        action.State = EnvironmentRestoreActionState.Failed;
                        await LogActionAsync(log, action, stopwatch.Elapsed, CancellationToken.None);
                    }
                    throw;
                }
                stage = "activation";
                await _host.ActivateServicesAsync(
                    prepared.Manifest.SystemdUnits, prepared.Manifest.DockerContainers, cancellationToken);
                foreach (var action in prepared.Plan.Actions.OfType<EnvironmentServiceRestoreAction>())
                {
                    action.State = EnvironmentRestoreActionState.Changed;
                    await LogActionAsync(log, action, stopwatch.Elapsed, cancellationToken);
                }
            }

            stopwatch.Stop();
            await LogAsync(log, "summary", "succeeded", null, stopwatch.Elapsed, cancellationToken);
            return Result(
                true, true, false, prepared.Plan, rollbackDirectory, null, null, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            resumeReport = await WriteResumeReportAsync(
                operationId, prepared?.Plan, rollbackDirectory, "operation_cancelled");
            if (log is not null)
                await LogAsync(log, "summary", "cancelled", "operation_cancelled", stopwatch.Elapsed, CancellationToken.None);
            return Result(
                false, applyStarted, true, prepared?.Plan, rollbackDirectory, resumeReport,
                "operation_cancelled", "Environment restore was cancelled.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var category = ex is EnvironmentRestoreException restore ? restore.Category : Categorize(ex, stage);
            resumeReport = options.Apply
                ? await WriteResumeReportAsync(operationId, prepared?.Plan, rollbackDirectory, category)
                : null;
            if (log is not null)
                await LogAsync(log, "summary", "failed", category, stopwatch.Elapsed, CancellationToken.None);
            return Result(
                false, applyStarted, false, prepared?.Plan, rollbackDirectory, resumeReport,
                category, SafeMessage(ex), stopwatch.Elapsed);
        }
        finally
        {
            if (log is not null)
            {
                try { await log.DisposeAsync(); }
                catch { }
            }
            DeleteDirectory(stagingDirectory);
        }
    }

    private async Task<PreparedRecovery> PrepareAsync(
        string artifactPath,
        EnvironmentRestoreOptions options,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var fullArtifactPath = Path.GetFullPath(artifactPath);
        var rootPath = Path.GetFullPath(options.RootPath);
        if (!Path.IsPathFullyQualified(options.RootPath) || !Directory.Exists(rootPath))
            throw new EnvironmentRestoreException("invalid_root", "Restore root must be an existing absolute directory.");
        EnsureNoSymlinkAncestors(rootPath);
        if (rootPath == Path.GetPathRoot(rootPath) && !_host.IsPrivileged)
            throw new EnvironmentRestoreException("privilege_required", "Restoring to the filesystem root requires a privileged process.");
        if (options.ActivateServices && rootPath != Path.GetPathRoot(rootPath))
            throw new EnvironmentRestoreException("activation_requires_system_root", "Service activation requires --root /.");

        var (descriptor, manifest, _) = await ValidateSetAsync(fullArtifactPath, cancellationToken);
        var payloadDirectory = Path.Combine(stagingDirectory, "payloads");
        CreatePrivateDirectory(payloadDirectory);
        var stagingRequiredBytes = manifest.Sources.Sum(source => source.SizeBytes);
        var stagingAvailableBytes = _host.GetAvailableBytes(stagingDirectory);
        if (stagingAvailableBytes < stagingRequiredBytes)
            throw new EnvironmentRestoreException(
                "insufficient_staging_space",
                "Insufficient staging space for recovery payloads.");
        var payloadPaths = await ExtractPayloadsAsync(
            fullArtifactPath, manifest, payloadDirectory, cancellationToken);

        var actions = new List<EnvironmentRestoreAction>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long destinationRequiredBytes = 0;
        long volumeExpandedBytes = 0;
        foreach (var source in manifest.Sources.OrderBy(source => source.RestoreOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Kind == EnvironmentRecoverySourceKind.File)
            {
                destinationRequiredBytes += await InspectFilePayloadAsync(
                    source, payloadPaths[source.Name], rootPath, actions,
                    destinations, caseInsensitiveDestinations, options.CreateRollbackCopy,
                    cancellationToken);
            }
            else
            {
                var target = ResolveVolumeTarget(source.Name);
                volumeExpandedBytes += await InspectVolumePayloadAsync(payloadPaths[source.Name], cancellationToken);
                var consumers = options.IncludeVolumes
                    ? (await _host.GetVolumeConsumersAsync(target.Volume, cancellationToken)).ToList()
                    : [];
                var undeclared = consumers.Except(manifest.DockerContainers, StringComparer.Ordinal).ToList();
                if (undeclared.Count > 0)
                    throw new EnvironmentRestoreException(
                        "volume_consumer_running",
                        $"Volume '{target.Volume}' has undeclared running consumers.");
                actions.Add(new EnvironmentVolumeRestoreAction
                {
                    Order = actions.Count,
                    SourceName = source.Name,
                    Destination = target.Volume,
                    VolumeName = target.Volume,
                    PayloadPath = source.PayloadPath,
                    Consumers = consumers,
                    State = options.IncludeVolumes
                        ? EnvironmentRestoreActionState.Planned
                        : EnvironmentRestoreActionState.Skipped
                });
            }
        }
        ValidatePlannedHierarchy(actions);

        foreach (var unit in manifest.SystemdUnits)
            actions.Add(ServiceAction(actions.Count, EnvironmentServiceKind.Systemd, unit, options.ActivateServices));
        foreach (var container in manifest.DockerContainers)
            actions.Add(ServiceAction(actions.Count, EnvironmentServiceKind.Docker, container, options.ActivateServices));

        var neededTools = new HashSet<string>(StringComparer.Ordinal);
        var restoringSystemRoot = rootPath == Path.GetPathRoot(rootPath);
        if (options.IncludeVolumes
            || (restoringSystemRoot && manifest.DockerContainers.Count > 0)
            || (options.ActivateServices && manifest.DockerContainers.Count > 0))
            neededTools.Add("docker");
        if ((restoringSystemRoot || options.ActivateServices) && manifest.SystemdUnits.Count > 0)
            neededTools.Add("systemctl");
        if (options.ActivateServices && manifest.SystemdUnits.Count > 0)
        {
            neededTools.Add("systemd-analyze");
        }
        var missingTools = new List<string>();
        foreach (var tool in neededTools.Order(StringComparer.Ordinal))
        {
            if (!await _host.ToolExistsAsync(tool, cancellationToken))
                missingTools.Add(tool);
        }

        var destinationAvailableBytes = _host.GetAvailableBytes(rootPath);
        var warnings = new List<string>
        {
            "Recovery artifacts are plaintext; checksum verification does not authenticate the sender."
        };
        if (!options.IncludeVolumes && manifest.Sources.Any(source => source.Kind == EnvironmentRecoverySourceKind.Volume))
            warnings.Add("Volume payloads are skipped unless --include-volumes is supplied.");
        if (!options.ActivateServices && (manifest.SystemdUnits.Count > 0 || manifest.DockerContainers.Count > 0))
            warnings.Add("Services and containers will remain stopped unless --activate-services is supplied.");
        if (manifest.Sources.Any(source => source.Kind == EnvironmentRecoverySourceKind.File))
            warnings.Add("Format version 1 does not encode owner/group metadata; mode and modification time are restored.");
        if (volumeExpandedBytes > 0 && options.IncludeVolumes)
        {
            warnings.Add($"Docker volume free space cannot be verified by Lutra; payload expands to {volumeExpandedBytes} bytes.");
            warnings.Add("Docker volume content cannot be compared safely and will be replaced on every apply.");
        }

        var requiredBytes = checked(stagingRequiredBytes + destinationRequiredBytes);
        var availableBytes = SaturatingAdd(stagingAvailableBytes, destinationAvailableBytes);

        var plan = new EnvironmentRestorePlan
        {
            ArtifactPath = fullArtifactPath,
            RootPath = rootPath,
            ArtifactId = manifest.ArtifactId,
            ArtifactSha256 = descriptor.Sha256,
            Actions = actions.OrderBy(action => action.Order).ThenBy(action => action.Destination, StringComparer.Ordinal).ToList(),
            RequiredBytes = requiredBytes,
            AvailableBytes = availableBytes,
            StagingRequiredBytes = stagingRequiredBytes,
            StagingAvailableBytes = stagingAvailableBytes,
            DestinationRequiredBytes = destinationRequiredBytes,
            DestinationAvailableBytes = destinationAvailableBytes,
            MissingTools = missingTools,
            Warnings = warnings,
            CanApply = missingTools.Count == 0
                       && stagingAvailableBytes >= stagingRequiredBytes
                       && destinationAvailableBytes >= destinationRequiredBytes
        };
        plan.ConfirmationToken = ComputePlanToken(plan, options);
        return new PreparedRecovery(
            descriptor,
            manifest,
            payloadPaths,
            plan);
    }

    private async Task<(
        EnvironmentRecoveryDescriptor Descriptor,
        EnvironmentRecoveryManifest Manifest,
        InventorySnapshot Inventory)> ValidateSetAsync(
        string artifactPath,
        CancellationToken cancellationToken)
    {
        var integrity = await BackupIntegrity.VerifyFileAsync(artifactPath, cancellationToken);
        if (!integrity.Success)
            throw new EnvironmentRestoreException("checksum_invalid", "Recovery set checksum verification failed.");

        EnvironmentRecoveryDescriptor descriptor;
        try
        {
            await using var descriptorStream = new FileStream(
                artifactPath + ".json", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            descriptor = await JsonSerializer.DeserializeAsync<EnvironmentRecoveryDescriptor>(
                             descriptorStream, JsonOptions, cancellationToken)
                         ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new EnvironmentRestoreException("descriptor_invalid", "Recovery descriptor is invalid.");
        }

        var info = new FileInfo(artifactPath);
        if (!descriptor.Success
            || descriptor.FormatVersion != EnvironmentRecoveryManifest.CurrentFormatVersion
            || string.IsNullOrWhiteSpace(descriptor.ArtifactId)
            || descriptor.CreatedAt.Kind != DateTimeKind.Utc
            || descriptor.Sources is null
            || descriptor.ArtifactFileName != info.Name
            || descriptor.FileSizeBytes != info.Length
            || descriptor.Sha256 is not { Length: 64 }
            || !descriptor.Sha256.Equals(integrity.ActualSha256, StringComparison.OrdinalIgnoreCase))
            throw new EnvironmentRestoreException("descriptor_mismatch", "Recovery descriptor does not match the artifact.");

        EnvironmentRecoveryManifest manifest;
        try
        {
            manifest = await EnvironmentRecoveryArchive.ValidateAsync(artifactPath, cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new EnvironmentRestoreException("archive_invalid", "Recovery archive validation failed.");
        }
        if (descriptor.Sources.Any(source => source is null
                                             || string.IsNullOrWhiteSpace(source.Name)
                                             || !Enum.IsDefined(source.Kind)))
            throw new EnvironmentRestoreException("descriptor_mismatch", "Recovery descriptor does not match the manifest.");
        var descriptorSources = descriptor.Sources
            .Select(source => (source.Name, source.Kind))
            .OrderBy(source => source.Name, StringComparer.Ordinal);
        var manifestSources = manifest.Sources
            .Select(source => (source.Name, source.Kind))
            .OrderBy(source => source.Name, StringComparer.Ordinal);
        if (descriptor.ArtifactId != manifest.ArtifactId
            || descriptor.CreatedAt != manifest.CreatedAt
            || descriptor.LutraVersion != manifest.LutraVersion
            || !descriptorSources.SequenceEqual(manifestSources))
            throw new EnvironmentRestoreException("descriptor_mismatch", "Recovery descriptor does not match the manifest.");
        InventorySnapshot inventory;
        try
        {
            var inventoryJson = await ReadOuterTextEntryAsync(
                artifactPath, "inventory/inventory.json", cancellationToken);
            inventory = JsonSerializer.Deserialize<InventorySnapshot>(inventoryJson, JsonOptions)
                        ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(inventory.Host)
                || string.IsNullOrWhiteSpace(inventory.LutraVersion)
                || inventory.Sections is null
                || inventory.Sections.Any(section => section is null || string.IsNullOrWhiteSpace(section.Name))
                || inventory.Sections.Select(section => section.Name).Distinct(StringComparer.Ordinal).Count()
                != inventory.Sections.Count)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new EnvironmentRestoreException("inventory_invalid", "Recovery inventory is invalid.");
        }
        return (descriptor, manifest, inventory);
    }

    private static async Task<string> ReadOuterTextEntryAsync(
        string artifactPath,
        string entryName,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry.Name != entryName)
                continue;
            using var text = new StreamReader(
                entry.DataStream!, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);
            return await text.ReadToEndAsync(cancellationToken);
        }
        throw new EnvironmentRestoreException("archive_invalid", "Recovery metadata entry is missing.");
    }

    private static async Task<Dictionary<string, string>> ExtractPayloadsAsync(
        string artifactPath,
        EnvironmentRecoveryManifest manifest,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var byPath = manifest.Sources.ToDictionary(source => source.PayloadPath, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var file = new FileStream(
            artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (!byPath.TryGetValue(entry.Name, out var source))
                continue;
            var outputPath = Path.Combine(outputDirectory, source.Name + ".tar.gz");
            await using var output = CreatePrivateFile(outputPath);
            await entry.DataStream!.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (output.Length != source.SizeBytes)
                throw new EnvironmentRestoreException("archive_changed", "Recovery payload changed during staging.");
            result.Add(source.Name, outputPath);
        }
        if (result.Count != manifest.Sources.Count)
            throw new EnvironmentRestoreException("archive_invalid", "Recovery payload extraction was incomplete.");
        foreach (var source in manifest.Sources)
        {
            var sha256 = await BackupIntegrity.ComputeSha256Async(result[source.Name], cancellationToken);
            if (!sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new EnvironmentRestoreException("archive_changed", "Recovery payload changed during staging.");
        }
        return result;
    }

    private async Task<long> InspectFilePayloadAsync(
        EnvironmentRecoverySource source,
        string payloadPath,
        string rootPath,
        List<EnvironmentRestoreAction> actions,
        HashSet<string> destinations,
        HashSet<string> caseInsensitiveDestinations,
        bool createRollbackCopy,
        CancellationToken cancellationToken)
    {
        long requiredBytes = 0;
        var initialActionCount = actions.Count;
        await using var file = new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = ValidateNestedEntry(entry);
            if (normalizedName.Length == 0)
                continue;
            var destination = ResolveDestination(rootPath, normalizedName);
            EnsureNotProtected(destination, rootPath);
            EnsureNoSymlinkPath(rootPath, destination);
            if (!destinations.Add(destination))
                throw new EnvironmentRestoreException("duplicate_destination", "Recovery payloads contain duplicate destinations.");
            if (!caseInsensitiveDestinations.Add(destination))
                throw new EnvironmentRestoreException("case_collision", "Recovery payloads contain case-colliding destinations.");

            if (entry.EntryType == TarEntryType.Directory)
            {
                if (File.Exists(destination))
                    throw new EnvironmentRestoreException("destination_conflict", "A file conflicts with a recovery directory.");
                actions.Add(new EnvironmentDirectoryRestoreAction
                {
                    Order = actions.Count,
                    SourceName = source.Name,
                    Destination = destination,
                    EntryName = entry.Name,
                    Mode = (int)entry.Mode,
                    ModificationTime = entry.ModificationTime,
                    State = Directory.Exists(destination)
                            && MetadataMatches(destination, (int)entry.Mode, entry.ModificationTime, directory: true)
                        ? EnvironmentRestoreActionState.Unchanged
                        : EnvironmentRestoreActionState.Planned
                });
                continue;
            }

            if (Directory.Exists(destination))
                throw new EnvironmentRestoreException("destination_conflict", "A directory conflicts with a recovery file.");
            var (size, sha256) = await HashAsync(entry.DataStream!, cancellationToken);
            var unchanged = File.Exists(destination)
                            && (await BackupIntegrity.ComputeSha256Async(destination, cancellationToken))
                            .Equals(sha256, StringComparison.OrdinalIgnoreCase)
                            && MetadataMatches(destination, (int)entry.Mode, entry.ModificationTime, directory: false);
            if (!unchanged)
            {
                requiredBytes += size;
                if (createRollbackCopy && File.Exists(destination))
                    requiredBytes += new FileInfo(destination).Length;
            }
            actions.Add(new EnvironmentFileRestoreAction
            {
                Order = actions.Count,
                SourceName = source.Name,
                Destination = destination,
                EntryName = entry.Name,
                SizeBytes = size,
                Sha256 = sha256,
                Mode = (int)entry.Mode,
                ModificationTime = entry.ModificationTime,
                State = unchanged ? EnvironmentRestoreActionState.Unchanged : EnvironmentRestoreActionState.Planned
            });
        }
        if (actions.Count == initialActionCount)
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery file payload is empty.");
        return requiredBytes;
    }

    private async Task ApplyFilePayloadAsync(
        EnvironmentRecoverySource source,
        string payloadPath,
        EnvironmentRestorePlan plan,
        string? rollbackDirectory,
        EnvironmentOperationLog log,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var actionMap = plan.Actions
            .Where(action => action.SourceName == source.Name)
            .ToDictionary(action => action is EnvironmentFileRestoreAction file ? file.EntryName :
                ((EnvironmentDirectoryRestoreAction)action).EntryName, StringComparer.Ordinal);
        await using var file = new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var directories = new List<EnvironmentDirectoryRestoreAction>();
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ValidateNestedEntry(entry).Length == 0)
                continue;
            var action = actionMap[entry.Name];
            EnsureNoSymlinkPath(plan.RootPath, action.Destination);
            if (action is EnvironmentDirectoryRestoreAction directoryAction)
            {
                if (!Directory.Exists(action.Destination))
                {
                    Directory.CreateDirectory(action.Destination);
                    action.State = EnvironmentRestoreActionState.Changed;
                }
                directories.Add(directoryAction);
                continue;
            }
            var fileAction = (EnvironmentFileRestoreAction)action;
            if (fileAction.State == EnvironmentRestoreActionState.Unchanged)
            {
                await LogActionAsync(log, fileAction, stopwatch.Elapsed, cancellationToken);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fileAction.Destination)!);
            if (rollbackDirectory is not null && File.Exists(fileAction.Destination))
            {
                var rollbackPath = Path.Combine(rollbackDirectory, fileAction.EntryName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
                File.Copy(fileAction.Destination, rollbackPath, overwrite: false);
                SetPrivateFileMode(rollbackPath);
            }
            var temporary = Path.Combine(
                Path.GetDirectoryName(fileAction.Destination)!, $".{Path.GetFileName(fileAction.Destination)}.lutra-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = CreatePrivateFile(temporary))
                    await entry.DataStream!.CopyToAsync(output, cancellationToken);
                ApplyMetadata(temporary, fileAction.Mode, fileAction.ModificationTime, false, plan.Warnings);
                File.Move(temporary, fileAction.Destination, overwrite: true);
                fileAction.State = EnvironmentRestoreActionState.Changed;
                await LogActionAsync(log, fileAction, stopwatch.Elapsed, cancellationToken);
            }
            catch
            {
                fileAction.State = EnvironmentRestoreActionState.Failed;
                await LogActionAsync(log, fileAction, stopwatch.Elapsed, CancellationToken.None);
                throw;
            }
            finally
            {
                DeleteFile(temporary);
            }
        }
        foreach (var directory in directories.OrderByDescending(action => action.Destination.Length))
        {
            ApplyMetadata(
                directory.Destination, directory.Mode, directory.ModificationTime, true, plan.Warnings);
            if (directory.State == EnvironmentRestoreActionState.Planned)
                directory.State = EnvironmentRestoreActionState.Changed;
            await LogActionAsync(log, directory, stopwatch.Elapsed, cancellationToken);
        }
    }

    private static async Task<long> InspectVolumePayloadAsync(
        string payloadPath,
        CancellationToken cancellationToken)
    {
        long expandedBytes = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var file = new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = ValidateNestedEntry(entry);
            if (name.Length == 0)
                continue;
            if (!names.Add(name))
                throw new EnvironmentRestoreException("duplicate_destination", "Volume payload contains duplicate destinations.");
            if (!caseInsensitiveNames.Add(name))
                throw new EnvironmentRestoreException("case_collision", "Volume payload contains case-colliding destinations.");
            if (entry.EntryType == TarEntryType.RegularFile)
                expandedBytes = checked(expandedBytes + (await HashAsync(entry.DataStream!, cancellationToken)).Size);
        }
        return expandedBytes;
    }

    private static string ValidateNestedEntry(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory))
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery payload contains an unsafe entry type.");
        if (string.IsNullOrWhiteSpace(entry.Name) || Path.IsPathRooted(entry.Name) || entry.Name.Contains('\\'))
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery payload contains an unsafe path.");
        var path = entry.Name.TrimEnd('/');
        if (path == "." && entry.EntryType == TarEntryType.Directory)
            return "";
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery payload contains an unsafe path.");
        if (entry.EntryType == TarEntryType.RegularFile && entry.DataStream is null)
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery payload file has no content.");
        return string.Join('/', segments);
    }

    private static string ResolveDestination(string rootPath, string entryName)
    {
        var relative = entryName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(rootPath, relative));
        if (!IsSameOrDescendant(destination, rootPath))
            throw new EnvironmentRestoreException("unsafe_payload", "Recovery destination escapes the selected root.");
        return destination;
    }

    private void EnsureNotProtected(string destination, string rootPath)
    {
        foreach (var protectedPath in new[] { _config.BackupDirectory, _config.StateDirectory! })
        {
            var logical = Path.Combine(rootPath, Path.GetFullPath(protectedPath).TrimStart(Path.DirectorySeparatorChar));
            if (IsSameOrDescendant(destination, logical))
                throw new EnvironmentRestoreException("protected_destination", "Recovery payload targets Lutra-managed storage.");
        }
    }

    private static void EnsureNoSymlinkPath(string rootPath, string destination)
    {
        var relative = Path.GetRelativePath(rootPath, destination);
        var current = rootPath;
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EnvironmentRestoreException("symlink_destination", "Recovery destination contains a symbolic link.");
            if (index < segments.Length - 1 && File.Exists(current))
                throw new EnvironmentRestoreException("destination_conflict", "A file conflicts with a recovery destination ancestor.");
        }
    }

    private static void ValidatePlannedHierarchy(IReadOnlyList<EnvironmentRestoreAction> actions)
    {
        var filesystemActions = actions
            .Where(action => action is EnvironmentFileRestoreAction or EnvironmentDirectoryRestoreAction)
            .ToList();
        foreach (var file in filesystemActions.OfType<EnvironmentFileRestoreAction>())
        {
            if (filesystemActions.Any(action => action != file
                                                && IsSameOrDescendant(action.Destination, file.Destination)))
                throw new EnvironmentRestoreException(
                    "destination_conflict",
                    "A recovery file conflicts with a descendant destination.");
        }
    }

    private static string ComputePlanToken(EnvironmentRestorePlan plan, EnvironmentRestoreOptions options)
    {
        var value = string.Join('\n',
            plan.ArtifactId,
            plan.ArtifactSha256,
            plan.RootPath,
            options.IncludeVolumes,
            options.ActivateServices,
            options.CreateRollbackCopy,
            plan.StagingRequiredBytes,
            plan.DestinationRequiredBytes,
            string.Join(',', plan.MissingTools),
            string.Join('\n', plan.Actions.Select(action => action switch
            {
                EnvironmentFileRestoreAction file =>
                    $"file|{file.SourceName}|{file.Destination}|{file.State}|{file.SizeBytes}|{file.Sha256}|{file.Mode}|{file.ModificationTime:O}",
                EnvironmentDirectoryRestoreAction directory =>
                    $"directory|{directory.SourceName}|{directory.Destination}|{directory.State}|{directory.Mode}|{directory.ModificationTime:O}",
                EnvironmentVolumeRestoreAction volume =>
                    $"volume|{volume.SourceName}|{volume.VolumeName}|{volume.State}|{string.Join(',', volume.Consumers)}",
                EnvironmentServiceRestoreAction service =>
                    $"service|{service.Kind}|{service.Name}|{service.State}",
                _ => throw new InvalidOperationException("unsupported_restore_action")
            })));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void EnsureNoSymlinkAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EnvironmentRestoreException("invalid_root", "Restore root cannot contain symbolic-link ancestors.");
        }
    }

    private static long SaturatingAdd(long first, long second)
        => first > long.MaxValue - second ? long.MaxValue : first + second;

    private VolumeTarget ResolveVolumeTarget(string sourceName)
        => _config.Volumes.SingleOrDefault(target => target.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
           ?? throw new EnvironmentRestoreException("target_missing", "A recovery volume target is not configured on this host.");

    private static EnvironmentVolumeRestoreAction FindVolumeAction(EnvironmentRestorePlan plan, string sourceName)
        => plan.Actions.OfType<EnvironmentVolumeRestoreAction>().Single(action => action.SourceName == sourceName);

    private static EnvironmentServiceRestoreAction ServiceAction(
        int order,
        EnvironmentServiceKind kind,
        string name,
        bool activate)
        => new()
        {
            Order = order,
            SourceName = "@services",
            Destination = name,
            Kind = kind,
            Name = name,
            State = activate ? EnvironmentRestoreActionState.Planned : EnvironmentRestoreActionState.Skipped
        };

    private static void ApplyMetadata(
        string path,
        int mode,
        DateTimeOffset modificationTime,
        bool directory,
        List<string> warnings)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, (UnixFileMode)mode);
            if (directory)
                Directory.SetLastWriteTimeUtc(path, modificationTime.UtcDateTime);
            else
                File.SetLastWriteTimeUtc(path, modificationTime.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Metadata could not be fully restored for '{Path.GetFileName(path)}'.");
        }
    }

    private static bool MetadataMatches(
        string path,
        int mode,
        DateTimeOffset modificationTime,
        bool directory)
    {
        try
        {
            var actualTime = directory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            if (Math.Abs((actualTime - modificationTime.UtcDateTime).TotalSeconds) >= 1)
                return false;
            return !OperatingSystem.IsLinux() || (int)File.GetUnixFileMode(path) == mode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string> WriteResumeReportAsync(
        string operationId,
        EnvironmentRestorePlan? plan,
        string? rollbackDirectory,
        string category)
    {
        var directory = Path.Combine(_config.StateDirectory!, "logs", "environment");
        CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, operationId + "-resume.json");
        var report = new
        {
            operation_id = operationId,
            error_category = category,
            rollback_directory = rollbackDirectory,
            actions = plan?.Actions.Select(action => new
            {
                action.SourceName,
                action.Destination,
                state = action.State.ToString().ToLowerInvariant()
            })
        };
        await using var output = CreatePrivateFile(path);
        await JsonSerializer.SerializeAsync(output, report, JsonOptions, CancellationToken.None);
        await output.WriteAsync("\n"u8.ToArray(), CancellationToken.None);
        return path;
    }

    private static async Task LogAsync(
        EnvironmentOperationLog log,
        string stage,
        string status,
        string? category,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try { await log.WriteAsync(stage, status, category, duration, cancellationToken); }
        catch { }
    }

    private static Task LogActionAsync(
        EnvironmentOperationLog log,
        EnvironmentRestoreAction action,
        TimeSpan duration,
        CancellationToken cancellationToken)
        => LogAsync(
            log,
            action switch
            {
                EnvironmentFileRestoreAction => "file_action",
                EnvironmentDirectoryRestoreAction => "directory_action",
                EnvironmentVolumeRestoreAction => "volume_action",
                EnvironmentServiceRestoreAction => "service_action",
                _ => "restore_action"
            },
            action.State.ToString().ToLowerInvariant(),
            null,
            duration,
            cancellationToken);

    private static async Task<(long Size, string Sha256)> HashAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static EnvironmentRestoreResult Result(
        bool success,
        bool applied,
        bool cancelled,
        EnvironmentRestorePlan? plan,
        string? rollbackDirectory,
        string? resumeReport,
        string? category,
        string? message,
        TimeSpan duration)
        => new()
        {
            Success = success,
            Applied = applied,
            Cancelled = cancelled,
            Plan = plan,
            RollbackDirectory = rollbackDirectory,
            ResumeReportPath = resumeReport,
            ErrorCategory = category,
            ErrorMessage = message,
            Duration = duration
        };

    private static string Categorize(Exception exception, string? stage = null)
        => exception switch
        {
            EnvironmentRestoreException restore => restore.Category,
            FileNotFoundException => "artifact_missing",
            UnauthorizedAccessException => "permission_denied",
            InvalidDataException => "archive_invalid",
            _ when stage is not null => stage + "_failed",
            _ => "inspection_failed"
        };

    private static string SafeMessage(Exception exception)
        => exception is EnvironmentRestoreException restore
            ? restore.Message
            : exception switch
            {
                FileNotFoundException => "Recovery artifact was not found.",
                UnauthorizedAccessException => "Recovery operation does not have sufficient permissions.",
                _ => "Environment recovery operation failed."
            };

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        else
            Directory.CreateDirectory(path);
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
                BufferSize = 81920,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = PrivateFileMode
            });
        }
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, PrivateFileMode);
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private sealed record PreparedRecovery(
        EnvironmentRecoveryDescriptor Descriptor,
        EnvironmentRecoveryManifest Manifest,
        Dictionary<string, string> PayloadPaths,
        EnvironmentRestorePlan Plan);
}

internal sealed class EnvironmentRestoreException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}
