using System.Text.Json;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Tests;

public sealed class IntegrityAndNamingTests
{
    [Fact]
    public void BuildFileName_IsReadableAndCollisionResistant()
    {
        var time = new DateTime(2026, 7, 21, 3, 4, 5, DateTimeKind.Utc);
        var first = BackupFileNaming.Build("app-db", time, "aaaaaaaaaaaa", ".dump", CompressionType.Gzip);
        var second = BackupFileNaming.Build("app-db", time, "bbbbbbbbbbbb", ".dump", CompressionType.Gzip);

        Assert.Equal("app-db_2026-07-21_030405_aaaaaaaaaaaa.dump.gz", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task VerifyFile_DetectsMutationAndValidSidecars()
    {
        using var temp = new TempDirectory();
        var backup = Path.Combine(temp.Path, "backup.tar");
        await File.WriteAllTextAsync(backup, "original");
        var checksum = await BackupIntegrity.ComputeSha256Async(backup);
        await BackupIntegrity.WriteChecksumFileAsync(backup, checksum);
        await BackupIntegrity.WriteManifestAsync(backup, new BackupManifest
        {
            TargetName = "files",
            TargetType = "files",
            BackupFileName = "backup.tar",
            FileSizeBytes = new FileInfo(backup).Length,
            Sha256 = checksum,
            Compression = CompressionType.None,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1,
            LutraVersion = "test",
            Success = true
        });

        Assert.True((await BackupIntegrity.VerifyFileAsync(backup)).Success);
        await File.AppendAllTextAsync(backup, "changed");
        var changed = await BackupIntegrity.VerifyFileAsync(backup);
        Assert.False(changed.Success);
        Assert.Equal("Checksum mismatch.", changed.Message);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(backup + ".json"));
        Assert.Equal("files", manifest.RootElement.GetProperty("target_type").GetString());
    }
}
