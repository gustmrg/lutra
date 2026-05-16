using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lutra.Core.Backup;

public static class BackupIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task WriteChecksumFileAsync(
        string backupFilePath,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var checksumPath = GetChecksumPath(backupFilePath);
        var line = $"{sha256}  {Path.GetFileName(backupFilePath)}{Environment.NewLine}";
        await File.WriteAllTextAsync(checksumPath, line, cancellationToken);
    }

    public static async Task WriteManifestAsync(
        string backupFilePath,
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(backupFilePath);
        await using var stream = new FileStream(
            manifestPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public static async Task<BackupFileVerificationResult> VerifyFileAsync(
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupFilePath))
            return BackupFileVerificationResult.Failed("Backup file does not exist.");

        var checksumPath = GetChecksumPath(backupFilePath);
        if (!File.Exists(checksumPath))
            return BackupFileVerificationResult.Failed("Checksum file does not exist.");

        var expected = await ReadChecksumAsync(checksumPath, cancellationToken);
        if (expected is null)
            return BackupFileVerificationResult.Failed("Checksum file is empty or invalid.");

        var actual = await ComputeSha256Async(backupFilePath, cancellationToken);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return BackupFileVerificationResult.Failed("Checksum mismatch.", expected, actual);

        var manifestPath = GetManifestPath(backupFilePath);
        if (!File.Exists(manifestPath))
            return BackupFileVerificationResult.Failed("Manifest file does not exist.", expected, actual);

        return BackupFileVerificationResult.Passed(expected, actual, manifestPath);
    }

    public static string GetChecksumPath(string backupFilePath) => backupFilePath + ".sha256";

    public static string GetManifestPath(string backupFilePath) => backupFilePath + ".json";

    private static async Task<string?> ReadChecksumAsync(string checksumPath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(checksumPath, cancellationToken);
        var firstToken = content
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return firstToken is { Length: 64 } ? firstToken : null;
    }
}

public sealed record BackupFileVerificationResult(
    bool Success,
    string Message,
    string? ExpectedSha256 = null,
    string? ActualSha256 = null,
    string? ManifestPath = null)
{
    public static BackupFileVerificationResult Passed(
        string expectedSha256,
        string actualSha256,
        string manifestPath) =>
        new(true, "Backup file checksum and manifest are valid.", expectedSha256, actualSha256, manifestPath);

    public static BackupFileVerificationResult Failed(
        string message,
        string? expectedSha256 = null,
        string? actualSha256 = null) =>
        new(false, message, expectedSha256, actualSha256);
}
