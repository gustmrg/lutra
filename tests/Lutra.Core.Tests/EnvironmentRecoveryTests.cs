using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Lutra.Core.Configuration;
using Lutra.Core.Files;
using Lutra.Core.Recovery;

namespace Lutra.Core.Tests;

public sealed class EnvironmentRecoveryTests
{
    [Fact]
    public async Task RecoveryArchive_WritesAtomicallyAndValidatesPayloads()
    {
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "app.tar.gz");
        await File.WriteAllTextAsync(payload, "application payload");
        var source = await SourceAsync("app", EnvironmentRecoverySourceKind.File, payload);
        var manifest = Manifest(source);
        var output = Path.Combine(temp.Path, "environment", "recovery.tar.gz");

        await EnvironmentRecoveryArchive.WriteAsync(
            output,
            manifest,
            new Dictionary<string, string> { ["app"] = payload },
            "{}\n",
            "# Inventory\n",
            "{}\n",
            "# Missing secrets\n",
            "# Restore\n");

        var parsed = await EnvironmentRecoveryArchive.ValidateAsync(output);

        Assert.Equal(manifest.ArtifactId, parsed.ArtifactId);
        Assert.Equal("app", Assert.Single(parsed.Sources).Name);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp"));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(output));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(output)!));
        }
    }

    [Fact]
    public async Task RecoveryArchive_RejectsPayloadThatDiffersFromManifest()
    {
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "app.tar.gz");
        await File.WriteAllTextAsync(payload, "changed");
        var source = new EnvironmentRecoverySource
        {
            Name = "app",
            Kind = EnvironmentRecoverySourceKind.File,
            PayloadPath = "payload/files/app.tar.gz",
            SizeBytes = 3,
            Sha256 = new string('a', 64),
            RestoreOrder = 0
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnvironmentRecoveryArchive.WriteAsync(
                Path.Combine(temp.Path, "recovery.tar.gz"),
                Manifest(source),
                new Dictionary<string, string> { ["app"] = payload },
                "{}", "inventory", "{}", "missing", "restore"));

        Assert.Contains("does not match", error.Message);
        Assert.False(File.Exists(Path.Combine(temp.Path, "recovery.tar.gz")));
    }

    [Fact]
    public async Task RecoveryArchive_RejectsUnsafeTarEntries()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "unsafe.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip, leaveOpen: false))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "../outside")
            {
                LinkName = "/etc/passwd"
            });
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => EnvironmentRecoveryArchive.ValidateAsync(archive));

        Assert.Contains("regular file", error.Message);
    }

    [Fact]
    public async Task RecoveryArchive_RejectsTraversalInRegularFileName()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "traversal.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip, leaveOpen: false))
        await using (var content = new MemoryStream("sentinel"u8.ToArray()))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../outside")
            {
                DataStream = content
            });
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => EnvironmentRecoveryArchive.ValidateAsync(archive));

        Assert.Contains("Unsafe recovery archive path", error.Message);
    }

    [Fact]
    public async Task RecoveryArchive_RejectsUnsupportedFormatVersion()
    {
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "app.tar.gz");
        await File.WriteAllTextAsync(payload, "payload");
        var source = await SourceAsync("app", EnvironmentRecoverySourceKind.File, payload);
        var manifest = Manifest(source);
        manifest = new EnvironmentRecoveryManifest
        {
            FormatVersion = 2,
            ArtifactId = manifest.ArtifactId,
            CreatedAt = manifest.CreatedAt,
            LutraVersion = manifest.LutraVersion,
            Sources = manifest.Sources
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnvironmentRecoveryArchive.WriteAsync(
                Path.Combine(temp.Path, "recovery.tar.gz"),
                manifest,
                new Dictionary<string, string> { ["app"] = payload },
                "{}", "inventory", "{}", "missing", "restore"));

        Assert.Contains("Unsupported recovery format version 2", error.Message);
    }

    [Fact]
    public async Task RecoveryArchive_RejectsUnknownSourceKind()
    {
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "app.tar.gz");
        await File.WriteAllTextAsync(payload, "payload");
        var source = await SourceAsync("app", EnvironmentRecoverySourceKind.File, payload);
        source = new EnvironmentRecoverySource
        {
            Name = source.Name,
            Kind = (EnvironmentRecoverySourceKind)99,
            PayloadPath = source.PayloadPath,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256,
            RestoreOrder = source.RestoreOrder
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnvironmentRecoveryArchive.WriteAsync(
                Path.Combine(temp.Path, "recovery.tar.gz"),
                Manifest(source),
                new Dictionary<string, string> { ["app"] = payload },
                "{}", "inventory", "{}", "missing", "restore"));

        Assert.Contains("Invalid source kind", error.Message);
    }

    [Fact]
    public async Task RecoveryArchive_CancellationRemovesTemporaryOutput()
    {
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "app.tar.gz");
        await File.WriteAllTextAsync(payload, "payload");
        var source = await SourceAsync("app", EnvironmentRecoverySourceKind.File, payload);
        var output = Path.Combine(temp.Path, "environment", "recovery.tar.gz");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EnvironmentRecoveryArchive.WriteAsync(
                output,
                Manifest(source),
                new Dictionary<string, string> { ["app"] = payload },
                "{}", "inventory", "{}", "missing", "restore", cancellation.Token));

        Assert.False(File.Exists(output));
        var outputDirectory = Path.GetDirectoryName(output)!;
        Assert.False(Directory.Exists(outputDirectory)
                     && Directory.EnumerateFiles(outputDirectory, "*.tmp").Any());
    }

    [Fact]
    public void DescriptorSerialization_IsStableAndUsesSnakeCaseEnums()
    {
        var descriptor = new EnvironmentRecoveryDescriptor
        {
            FormatVersion = 1,
            ArtifactId = "artifact",
            CreatedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            ArtifactFileName = "environment.tar.gz",
            FileSizeBytes = 42,
            Sha256 = new string('a', 64),
            LutraVersion = "1.0.0",
            Sources =
            [
                new EnvironmentRecoveryDescriptorSource
                {
                    Name = "uploads",
                    Kind = EnvironmentRecoverySourceKind.Volume
                }
            ],
            Success = true
        };

        var first = EnvironmentRecoveryArchive.Serialize(descriptor);
        var second = EnvironmentRecoveryArchive.Serialize(descriptor);

        Assert.Equal(first, second);
        Assert.Contains("\"format_version\"", first);
        Assert.Contains("\"kind\": \"volume\"", first);
    }

    [Fact]
    public void PlaintextRecovery_HasNonOverridableCommonSecretExclusions()
    {
        Assert.Contains(".env", EnvironmentBackupConfig.MandatorySecretExcludes);
        Assert.Contains("*.key", EnvironmentBackupConfig.MandatorySecretExcludes);
        Assert.Contains(".ssh", EnvironmentBackupConfig.MandatorySecretExcludes);
        Assert.Contains("credentials*", EnvironmentBackupConfig.MandatorySecretExcludes);
    }

    [Fact]
    public async Task FileArchive_StreamOutputAppliesMandatorySecretExclusions()
    {
        using var temp = new TempDirectory();
        var sourceDirectory = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "config.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, ".env"), "SECRET=sentinel");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "server.key"), "sentinel");
        await using var output = new MemoryStream();

        await FileArchive.CreateAsync(
            [sourceDirectory],
            EnvironmentBackupConfig.MandatorySecretExcludes,
            output,
            CompressionType.Gzip);

        Assert.True(output.CanWrite);
        output.Position = 0;
        await using var gzip = new GZipStream(output, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip);
        var names = new List<string>();
        while (await reader.GetNextEntryAsync() is { } entry)
            names.Add(entry.Name);

        Assert.Contains(names, name => name.EndsWith("config.json", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.EndsWith(".env", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.EndsWith("server.key", StringComparison.Ordinal));
    }

    private static async Task<EnvironmentRecoverySource> SourceAsync(
        string name,
        EnvironmentRecoverySourceKind kind,
        string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var directory = kind == EnvironmentRecoverySourceKind.File ? "files" : "volumes";
        return new EnvironmentRecoverySource
        {
            Name = name,
            Kind = kind,
            PayloadPath = $"payload/{directory}/{name}.tar.gz",
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            RestoreOrder = kind == EnvironmentRecoverySourceKind.File ? 0 : 1
        };
    }

    private static EnvironmentRecoveryManifest Manifest(params EnvironmentRecoverySource[] sources)
        => new()
        {
            FormatVersion = EnvironmentRecoveryManifest.CurrentFormatVersion,
            ArtifactId = "20260805-test",
            CreatedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            LutraVersion = "1.0.0-test",
            Sources = sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToList()
        };
}
