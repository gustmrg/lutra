using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lutra.Core.Recovery;

/// <summary>Writes and validates versioned environment recovery archives.</summary>
public static class EnvironmentRecoveryArchive
{
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static readonly HashSet<string> RequiredMetadataEntries =
    [
        "manifest.json",
        "inventory/inventory.json",
        "inventory/inventory.md",
        "reports/backup.json",
        "MISSING_SECRETS.md",
        "RESTORE.md"
    ];

    public static async Task WriteAsync(
        string outputPath,
        EnvironmentRecoveryManifest manifest,
        IReadOnlyDictionary<string, string> payloadFiles,
        string inventoryJson,
        string inventoryMarkdown,
        string backupReportJson,
        string missingSecretsMarkdown,
        string restoreMarkdown,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        ValidatePayloadMap(manifest, payloadFiles);
        await ValidatePayloadFilesAsync(manifest, payloadFiles, cancellationToken);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        CreatePrivateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = CreatePrivateFile(tempPath))
            {
                await using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
                await using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

                await WriteTextEntryAsync(writer, "manifest.json", Serialize(manifest), cancellationToken);
                await WriteTextEntryAsync(writer, "inventory/inventory.json", inventoryJson, cancellationToken);
                await WriteTextEntryAsync(writer, "inventory/inventory.md", inventoryMarkdown, cancellationToken);

                foreach (var source in manifest.Sources)
                    await WriteFileEntryAsync(writer, source.PayloadPath, payloadFiles[source.Name], cancellationToken);

                await WriteTextEntryAsync(writer, "reports/backup.json", backupReportJson, cancellationToken);
                await WriteTextEntryAsync(writer, "MISSING_SECRETS.md", missingSecretsMarkdown, cancellationToken);
                await WriteTextEntryAsync(writer, "RESTORE.md", restoreMarkdown, cancellationToken);
            }

            _ = await ValidateAsync(tempPath, cancellationToken);
            File.Move(tempPath, fullOutputPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    public static async Task<EnvironmentRecoveryManifest> ValidateAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var payloads = new Dictionary<string, (long Size, string Sha256)>(StringComparer.Ordinal);
        EnvironmentRecoveryManifest? manifest = null;

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = ValidateEntry(entry);
            if (!seen.Add(name))
                throw new InvalidDataException($"Recovery archive contains duplicate entry '{name}'.");
            if (entry.DataStream is null)
                throw new InvalidDataException($"Recovery archive entry '{name}' has no data.");

            if (name == "manifest.json")
            {
                var bytes = await ReadMetadataAsync(entry.DataStream, name, cancellationToken);
                manifest = JsonSerializer.Deserialize<EnvironmentRecoveryManifest>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Recovery manifest is empty.");
                ValidateManifest(manifest);
                continue;
            }

            if (name.StartsWith("payload/", StringComparison.Ordinal))
            {
                var (size, sha256) = await HashAsync(entry.DataStream, cancellationToken);
                payloads.Add(name, (size, sha256));
                continue;
            }

            if (!RequiredMetadataEntries.Contains(name))
                throw new InvalidDataException($"Recovery archive contains undeclared entry '{name}'.");

            _ = await ReadMetadataAsync(entry.DataStream, name, cancellationToken);
        }

        if (manifest is null)
            throw new InvalidDataException("Recovery archive does not contain manifest.json.");

        var expectedEntries = RequiredMetadataEntries
            .Concat(manifest.Sources.Select(source => source.PayloadPath))
            .ToHashSet(StringComparer.Ordinal);
        if (!seen.SetEquals(expectedEntries))
            throw new InvalidDataException("Recovery archive entries do not match the manifest.");

        foreach (var source in manifest.Sources)
        {
            var payload = payloads[source.PayloadPath];
            if (payload.Size != source.SizeBytes
                || !payload.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Payload '{source.PayloadPath}' does not match its manifest checksum.");
            }
        }

        return manifest;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions) + "\n";

    private static void ValidateManifest(EnvironmentRecoveryManifest manifest)
    {
        if (manifest.FormatVersion != EnvironmentRecoveryManifest.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported recovery format version {manifest.FormatVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.ArtifactId) || string.IsNullOrWhiteSpace(manifest.LutraVersion))
            throw new InvalidDataException("Recovery manifest identity fields are required.");
        if (manifest.CreatedAt.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("Recovery manifest created_at must be UTC.");
        if (manifest.Sources is null || manifest.Sources.Count == 0)
            throw new InvalidDataException("Recovery manifest must contain at least one source.");
        if (manifest.RequiredTools is null
            || manifest.SystemdUnits is null
            || manifest.DockerContainers is null)
            throw new InvalidDataException("Recovery manifest lists cannot be null.");
        if (manifest.RequiredTools.Any(tool => tool is not ("docker" or "systemctl"))
            || manifest.RequiredTools.Distinct(StringComparer.Ordinal).Count() != manifest.RequiredTools.Count)
            throw new InvalidDataException("Recovery manifest required_tools contains unsupported values.");
        if (manifest.SystemdUnits.Any(unit => !IsSafeRuntimeName(unit) || !unit.EndsWith(".service", StringComparison.Ordinal))
            || manifest.SystemdUnits.Distinct(StringComparer.Ordinal).Count() != manifest.SystemdUnits.Count)
            throw new InvalidDataException("Recovery manifest systemd_units contains invalid values.");
        if (manifest.DockerContainers.Any(name => !IsSafeRuntimeName(name))
            || manifest.DockerContainers.Distinct(StringComparer.Ordinal).Count() != manifest.DockerContainers.Count)
            throw new InvalidDataException("Recovery manifest docker_containers contains invalid values.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? previousName = null;
        foreach (var source in manifest.Sources)
        {
            if (source is null)
                throw new InvalidDataException("Recovery sources cannot contain null entries.");
            if (string.IsNullOrWhiteSpace(source.Name)
                || source.Name is "." or ".."
                || source.Name.Contains('/')
                || source.Name.Contains('\\')
                || source.Name.Any(char.IsControl)
                || !names.Add(source.Name))
                throw new InvalidDataException("Recovery source names must be nonempty and unique.");
            if (previousName is not null && StringComparer.Ordinal.Compare(previousName, source.Name) >= 0)
                throw new InvalidDataException("Recovery sources must be ordered by name.");
            previousName = source.Name;

            if (!Enum.IsDefined(source.Kind))
                throw new InvalidDataException($"Invalid source kind for '{source.Name}'.");

            var expectedPrefix = source.Kind == EnvironmentRecoverySourceKind.File
                ? "payload/files/"
                : "payload/volumes/";
            ValidateSafePath(source.PayloadPath);
            if (source.PayloadPath != $"{expectedPrefix}{source.Name}.tar.gz"
                || !paths.Add(source.PayloadPath))
                throw new InvalidDataException($"Invalid payload path '{source.PayloadPath}'.");
            if (source.SizeBytes < 0 || !IsSha256(source.Sha256))
                throw new InvalidDataException($"Invalid payload metadata for '{source.Name}'.");
            if (source.RestoreOrder < 0)
                throw new InvalidDataException($"Invalid restore order for '{source.Name}'.");
        }

        if (manifest.Sources.Select(source => source.RestoreOrder).Distinct().Count() != manifest.Sources.Count)
            throw new InvalidDataException("Recovery source restore_order values must be unique.");
    }

    private static void ValidatePayloadMap(
        EnvironmentRecoveryManifest manifest,
        IReadOnlyDictionary<string, string> payloadFiles)
    {
        var expectedNames = manifest.Sources.Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
        if (!expectedNames.SetEquals(payloadFiles.Keys))
            throw new InvalidDataException("Payload files do not match recovery manifest sources.");

        foreach (var path in payloadFiles.Values)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Recovery payload does not exist: {path}", path);
        }
    }

    private static async Task ValidatePayloadFilesAsync(
        EnvironmentRecoveryManifest manifest,
        IReadOnlyDictionary<string, string> payloadFiles,
        CancellationToken cancellationToken)
    {
        foreach (var source in manifest.Sources)
        {
            await using var stream = new FileStream(
                payloadFiles[source.Name], FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var payload = await HashAsync(stream, cancellationToken);
            if (payload.Size != source.SizeBytes
                || !payload.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Payload file for '{source.Name}' does not match its manifest checksum.");
            }
        }
    }

    private static string ValidateEntry(TarEntry entry)
    {
        if (entry.EntryType != TarEntryType.RegularFile)
            throw new InvalidDataException($"Recovery archive entry '{entry.Name}' must be a regular file.");
        ValidateSafePath(entry.Name);
        return entry.Name;
    }

    private static void ValidateSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe recovery archive path '{path}'.");
        }
    }

    private static async Task WriteTextEntryAsync(
        TarWriter writer,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        await using (stream)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }
    }

    private static async Task WriteFileEntryAsync(
        TarWriter writer,
        string name,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream };
        await writer.WriteEntryAsync(entry, cancellationToken);
    }

    private static async Task<byte[]> ReadMetadataAsync(
        Stream stream,
        string name,
        CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > MaximumMetadataBytes)
                throw new InvalidDataException($"Recovery metadata entry '{name}' exceeds the size limit.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

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

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSafeRuntimeName(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 255
           && value[0] != '-'
           && value.All(character => char.IsAsciiLetterOrDigit(character)
                                     || character is '.' or '_' or '-' or '@');

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
            return;
        }

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
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                UnixCreateMode = PrivateFileMode
            });
        }

        return new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
