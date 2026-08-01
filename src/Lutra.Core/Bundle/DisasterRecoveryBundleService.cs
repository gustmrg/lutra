using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Encryption;
using Lutra.Core.History;

namespace Lutra.Core.Bundle;

public sealed class DisasterRecoveryBundleService
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;

    public DisasterRecoveryBundleService(BackupConfig config, IBackupHistoryService history)
    {
        _config = config;
        _history = history;
    }

    public async Task<BundleResult> CreateAsync(
        string configPath,
        string envPath,
        string? outputPath,
        bool encrypt,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var bundleDirectory = Path.Combine(_config.BackupDirectory, "bundles");
        Directory.CreateDirectory(bundleDirectory);
        var finalPath = outputPath ?? Path.Combine(bundleDirectory,
            $"lutra-recovery_{startedAt:yyyy-MM-dd_HHmmss}_{Guid.NewGuid().ToString("N")[..12]}.tar.gz" + (encrypt ? ".age" : ""));
        finalPath = Path.GetFullPath(finalPath);
        if (encrypt && !finalPath.EndsWith(".age", StringComparison.OrdinalIgnoreCase))
            finalPath += ".age";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (encrypt && _config.Encryption is null)
            return new BundleResult(false, null, "--encrypt requires global age encryption configuration.", 0);

        var staging = Path.Combine(Path.GetTempPath(), $"lutra-bundle-{Guid.NewGuid():N}");
        var plainTemp = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(staging);
            var records = await _history.GetAllRecordsAsync(cancellationToken);
            var included = 0;
            foreach (var target in _config.AllTargets())
            {
                var latest = records.FirstOrDefault(record => record.TargetName == target.Name
                    && record.Status == HistoryOperationStatus.Succeeded
                    && record.OperationType == HistoryOperationType.Backup
                    && !string.IsNullOrWhiteSpace(record.FileName));
                if (latest is null)
                    throw new InvalidOperationException($"Target '{target.Name}' has no successful backup to bundle.");

                var source = Path.Combine(_config.BackupDirectory, target.Name, latest.FileName!);
                if (!File.Exists(source))
                    throw new FileNotFoundException($"Latest backup for '{target.Name}' is missing: {source}");
                var destinationDirectory = Path.Combine(staging, "backups", target.Name);
                Directory.CreateDirectory(destinationDirectory);
                CopyArtifact(source, destinationDirectory);
                included++;
            }

            foreach (var database in _config.Databases.Where(database => database.PostgresWalArchivePath is not null))
            {
                var walTarget = database.Name + "-wal";
                var latestWal = records.FirstOrDefault(record => record.TargetName == walTarget
                    && record.Status == HistoryOperationStatus.Succeeded
                    && record.OperationType == HistoryOperationType.Backup
                    && !string.IsNullOrWhiteSpace(record.FileName));
                if (latestWal is null)
                    throw new InvalidOperationException($"PostgreSQL target '{database.Name}' has no WAL archive backup to bundle.");
                var source = Path.Combine(_config.BackupDirectory, walTarget, latestWal.FileName!);
                if (!File.Exists(source))
                    throw new FileNotFoundException($"Latest WAL archive for '{database.Name}' is missing: {source}");
                CopyArtifact(source, Path.Combine(staging, "backups", walTarget));
            }

            var inventoryDirectory = Path.Combine(_config.BackupDirectory, "inventory");
            var inventory = Directory.Exists(inventoryDirectory)
                ? Directory.GetFiles(inventoryDirectory, "inventory_*.md").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (inventory is not null)
                CopyArtifact(inventory, Path.Combine(staging, "inventory"));

            var referenceDirectory = Path.Combine(staging, "reference");
            Directory.CreateDirectory(referenceDirectory);
            if (File.Exists(configPath))
                File.Copy(configPath, Path.Combine(referenceDirectory, "lutra.yaml"));
            await File.WriteAllTextAsync(
                Path.Combine(referenceDirectory, "ENVIRONMENT_VARIABLES.txt"),
                BuildEnvironmentReference(envPath), cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(staging, "RESTORE.md"), BuildRestoreInstructions(inventory is not null), cancellationToken);

            await CreateTarGzipAsync(staging, plainTemp, cancellationToken);
            if (encrypt)
            {
                await AgeEncryption.EncryptAsync(
                    plainTemp, finalPath, _config.Encryption!.Recipient, cancellationToken);
                File.Delete(plainTemp);
            }
            else
            {
                File.Move(plainTemp, finalPath);
            }

            var checksum = await BackupIntegrity.ComputeSha256Async(finalPath, cancellationToken);
            await BackupIntegrity.WriteChecksumFileAsync(finalPath, checksum, cancellationToken);
            return new BundleResult(true, finalPath, null, included);
        }
        catch (Exception ex)
        {
            DeleteIfExists(plainTemp);
            DeleteIfExists(finalPath);
            DeleteIfExists(BackupIntegrity.GetChecksumPath(finalPath));
            return new BundleResult(false, null, ex.Message, 0);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static async Task CreateTarGzipAsync(string sourceDirectory, string outputPath, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await TarFile.CreateFromDirectoryAsync(sourceDirectory, gzip, includeBaseDirectory: false, cancellationToken: cancellationToken);
    }

    private static void CopyArtifact(string source, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, Path.Combine(destinationDirectory, Path.GetFileName(source)));
        foreach (var sidecar in new[] { BackupIntegrity.GetChecksumPath(source), BackupIntegrity.GetManifestPath(source) })
        {
            if (File.Exists(sidecar))
                File.Copy(sidecar, Path.Combine(destinationDirectory, Path.GetFileName(sidecar)));
        }
    }

    private static string BuildEnvironmentReference(string envPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Configured env file: {Path.GetFileName(envPath)}");
        builder.AppendLine("Required variable names (values intentionally omitted):");
        if (!File.Exists(envPath))
            return builder.AppendLine("- env file was not present when the bundle was created").ToString();

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator > 0 && !trimmed.StartsWith('#'))
                builder.AppendLine($"- {trimmed[..separator].Trim()}");
        }
        return builder.ToString();
    }

    private string BuildRestoreInstructions(bool hasInventory)
    {
        var builder = new StringBuilder("""
            # Lutra Disaster Recovery Bundle

            This bundle contains the latest successful artifact for every configured target.

            ## Rebuild outline

            1. Provision a clean server and recreate users, SSH access, firewall rules, packages, and Docker using your runbook/IaC.
            2. Install Docker and Lutra.
            3. Review `reference/lutra.yaml` and recreate the environment variables listed in `reference/ENVIRONMENT_VARIABLES.txt`.
            4. Copy each artifact and its sidecars from `backups/<target>/` to a working directory.
            5. If an artifact ends in `.age`, decrypt it with the offline age identity key.
            6. Recreate containers and empty Docker volumes, then restore targets in the order below.

            ## Target restore order

            """);
        foreach (var target in _config.Files)
            builder.AppendLine($"- Files `{target.Name}`: `lutra restore --target {target.Name} --file <artifact> --force`");
        foreach (var target in _config.Volumes)
            builder.AppendLine($"- Volume `{target.Name}`: stop consumers, then `lutra restore --target {target.Name} --file <artifact> --force`");
        foreach (var target in _config.Databases)
        {
            builder.AppendLine($"- Database `{target.Name}`: start `{target.Container}`, then `lutra restore --target {target.Name} --file <artifact> --force`");
            if (target.PostgresWalArchivePath is not null)
                builder.AppendLine($"  - WAL archive `{target.Name}-wal` is included for PITR; restore it using the PostgreSQL recovery configuration in your runbook.");
        }
        builder.AppendLine();
        builder.AppendLine(hasInventory
            ? "Review `inventory/` for the captured package, service, Docker, cron, and firewall inventory."
            : "No server inventory snapshot was available when this bundle was created.");
        builder.AppendLine();
        builder.AppendLine("## Not covered\n\nThis bundle does not recreate system packages, users, firewall rules, SSH keys, or other full system state. Those belong in a rebuild runbook or IaC.");
        return builder.ToString();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

public sealed record BundleResult(bool Success, string? FilePath, string? ErrorMessage, int TargetCount);
