using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Inventory;
using Lutra.Core.Recovery;

namespace Lutra.Core.Tests;

public sealed class EnvironmentRestoreServiceTests
{
    [Fact]
    public async Task Inspect_ValidatesChecksumDescriptorAndManifest()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config.txt", "value")]);
        var service = new EnvironmentRestoreService(fixture.Config, new FakeHost());

        var result = await service.InspectAsync(fixture.ArtifactPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ChecksumValid);
        Assert.Equal("fixture", result.Manifest!.ArtifactId);
        Assert.Equal("files", Assert.Single(result.Manifest.Sources).Name);
    }

    [Fact]
    public async Task Inspect_MissingOrBadChecksumFailsSafely()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config.txt", "value")]);
        File.WriteAllText(fixture.ArtifactPath + ".sha256", $"{new string('0', 64)}  artifact.tar.gz\n");
        var service = new EnvironmentRestoreService(fixture.Config, new FakeHost());

        var result = await service.InspectAsync(fixture.ArtifactPath);

        Assert.False(result.Success);
        Assert.Equal("checksum_invalid", result.ErrorCategory);
        Assert.DoesNotContain("value", result.ErrorMessage);
    }

    [Fact]
    public async Task Inspect_ReportsValidChecksumWhenDescriptorIsMalformed()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config.txt", "value")]);
        await File.WriteAllTextAsync(fixture.ArtifactPath + ".json", "{}");

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .InspectAsync(fixture.ArtifactPath);

        Assert.False(result.Success);
        Assert.True(result.ChecksumValid);
        Assert.Equal("descriptor_invalid", result.ErrorCategory);
    }

    [Fact]
    public async Task Restore_DryRunIsImmutableThenApplyConvergesWithRollbackCopy()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [
            DirectoryEntry("./", Convert.ToInt32("0755", 8)),
            DirectoryEntry("etc/app/", Convert.ToInt32("0750", 8)),
            FileEntry("etc/app/config.txt", "new-value", Convert.ToInt32("0640", 8))
        ]);
        var root = Path.Combine(temp.Path, "root");
        var destination = Path.Combine(root, "etc", "app", "config.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old-value");
        var service = new EnvironmentRestoreService(fixture.Config, new FakeHost());

        var dryRun = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root));

        Assert.True(dryRun.Success, dryRun.ErrorMessage);
        Assert.False(dryRun.Applied);
        Assert.Equal("old-value", await File.ReadAllTextAsync(destination));
        Assert.Contains(dryRun.Plan!.Actions, action =>
            action is EnvironmentFileRestoreAction { State: EnvironmentRestoreActionState.Planned });

        var applied = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.True(applied.Success, applied.ErrorMessage);
        Assert.Equal("new-value", await File.ReadAllTextAsync(destination));
        Assert.Equal(
            "old-value",
            await File.ReadAllTextAsync(Path.Combine(applied.RollbackDirectory!, "etc", "app", "config.txt")));
        if (OperatingSystem.IsLinux())
            Assert.Equal((UnixFileMode)Convert.ToInt32("0640", 8), File.GetUnixFileMode(destination));

        var repeated = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.True(repeated.Success, repeated.ErrorMessage);
        Assert.Contains(repeated.Plan!.Actions, action =>
            action is EnvironmentFileRestoreAction { State: EnvironmentRestoreActionState.Unchanged });
        Assert.Equal("new-value", await File.ReadAllTextAsync(destination));
    }

    [Theory]
    [InlineData("../outside", TarEntryType.RegularFile, "unsafe_payload")]
    [InlineData("etc/link", TarEntryType.SymbolicLink, "unsafe_payload")]
    [InlineData("dev/node", TarEntryType.BlockDevice, "unsafe_payload")]
    public async Task Restore_RejectsUnsafeNestedEntriesBeforeMutation(
        string entryName,
        TarEntryType entryType,
        string category)
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [new NestedEntry(entryName, entryType, "sentinel-secret")]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var service = new EnvironmentRestoreService(fixture.Config, new FakeHost());

        var result = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.False(result.Success);
        Assert.Equal(category, result.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.DoesNotContain("sentinel-secret", result.ErrorMessage);
    }

    [Fact]
    public async Task Restore_RejectsCaseCollisionsAndSymlinkAncestors()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var temp = new TempDirectory();
        var collision = await CreateFileSetAsync(temp, [
            FileEntry("etc/App.conf", "one"),
            FileEntry("etc/app.conf", "two")
        ]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var service = new EnvironmentRestoreService(collision.Config, new FakeHost());

        var collisionResult = await service.RestoreAsync(
            collision.ArtifactPath, new EnvironmentRestoreOptions(root));

        Assert.False(collisionResult.Success);
        Assert.Equal("case_collision", collisionResult.ErrorCategory);

        using var second = new TempDirectory();
        var symlinkFixture = await CreateFileSetAsync(second, [FileEntry("etc/app/config", "value")]);
        var symlinkRoot = Path.Combine(second.Path, "root");
        var outside = Path.Combine(second.Path, "outside");
        Directory.CreateDirectory(symlinkRoot);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(symlinkRoot, "etc"), outside);

        var symlinkResult = await new EnvironmentRestoreService(symlinkFixture.Config, new FakeHost())
            .RestoreAsync(symlinkFixture.ArtifactPath, new EnvironmentRestoreOptions(symlinkRoot));

        Assert.False(symlinkResult.Success);
        Assert.Equal("symlink_destination", symlinkResult.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Restore_RejectsSymlinkAboveSelectedRoot()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config", "value")]);
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "root"));
        var link = Path.Combine(temp.Path, "linked-parent");
        Directory.CreateSymbolicLink(link, outside);

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(
                fixture.ArtifactPath,
                new EnvironmentRestoreOptions(Path.Combine(link, "root"), Apply: true));

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Equal("invalid_root", result.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(outside, "root")));
    }

    [Fact]
    public async Task Restore_VolumesRequireOptInAndDeclaredStoppedConsumers()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateVolumeSetAsync(temp, dockerContainers: ["app"]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var host = new FakeHost { Consumers = ["app"] };
        var service = new EnvironmentRestoreService(fixture.Config, host);

        var skipped = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.True(skipped.Success, skipped.ErrorMessage);
        Assert.Empty(host.RestoredVolumes);
        Assert.Equal(EnvironmentRestoreActionState.Skipped,
            Assert.Single(skipped.Plan!.Actions.OfType<EnvironmentVolumeRestoreAction>()).State);

        var restored = await service.RestoreAsync(
            fixture.ArtifactPath,
            new EnvironmentRestoreOptions(root, Apply: true, IncludeVolumes: true));

        Assert.True(restored.Success, restored.ErrorMessage);
        Assert.Equal(["app"], host.StoppedContainers);
        Assert.Equal(["app_data"], host.RestoredVolumes);

        host.Consumers = ["unknown"];
        var rejected = await service.RestoreAsync(
            fixture.ArtifactPath,
            new EnvironmentRestoreOptions(root, Apply: true, IncludeVolumes: true));
        Assert.False(rejected.Success);
        Assert.Equal("volume_consumer_running", rejected.ErrorCategory);
    }

    [Fact]
    public async Task Restore_FailedValidationPreventsActivation()
    {
        using var temp = new TempDirectory();
        var writableEntry = Path.Combine(temp.Path, "activation", "marker.txt")
            .TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
        var fixture = await CreateFileSetAsync(
            temp,
            [FileEntry(writableEntry, "marker")],
            systemdUnits: ["app.service"]);
        var host = new FakeHost { FailValidation = true };
        var service = new EnvironmentRestoreService(fixture.Config, host);

        var result = await service.RestoreAsync(
            fixture.ArtifactPath,
            new EnvironmentRestoreOptions("/", Apply: true, ActivateServices: true));

        Assert.False(result.Success);
        Assert.Equal("validation_failed", result.ErrorCategory);
        Assert.False(host.Activated);
        Assert.DoesNotContain("sentinel-secret", result.ErrorMessage);
        var logs = Directory.GetFiles(Path.Combine(fixture.Config.StateDirectory!, "logs", "environment"), "*.jsonl");
        var log = await File.ReadAllTextAsync(logs.Order().Last());
        Assert.Contains("\"stage\":\"service_action\"", log);
        Assert.Contains("\"status\":\"failed\"", log);
        Assert.DoesNotContain("sentinel-secret", log);
    }

    [Fact]
    public async Task Restore_ExplicitActivationRunsOnlyAfterValidation()
    {
        using var temp = new TempDirectory();
        var writableEntry = Path.Combine(temp.Path, "activation-success", "marker.txt")
            .TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
        var fixture = await CreateFileSetAsync(
            temp, [FileEntry(writableEntry, "marker")], systemdUnits: ["app.service"]);
        var host = new FakeHost();

        var result = await new EnvironmentRestoreService(fixture.Config, host)
            .RestoreAsync(
                fixture.ArtifactPath,
                new EnvironmentRestoreOptions("/", Apply: true, ActivateServices: true));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(host.Validated);
        Assert.True(host.Activated);
        Assert.All(result.Plan!.Actions.OfType<EnvironmentServiceRestoreAction>(),
            action => Assert.Equal(EnvironmentRestoreActionState.Changed, action.State));
    }

    [Fact]
    public async Task Restore_CancellationWritesSanitizedResumeAndSummaryLog()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateVolumeSetAsync(temp, dockerContainers: ["app"]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var host = new FakeHost { CancelVolumeRestore = true };
        var service = new EnvironmentRestoreService(fixture.Config, host);

        var result = await service.RestoreAsync(
            fixture.ArtifactPath,
            new EnvironmentRestoreOptions(root, Apply: true, IncludeVolumes: true));

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal("operation_cancelled", result.ErrorCategory);
        Assert.True(File.Exists(result.ResumeReportPath));
        Assert.DoesNotContain("sentinel-secret", await File.ReadAllTextAsync(result.ResumeReportPath!));
        var logs = Directory.GetFiles(Path.Combine(fixture.Config.StateDirectory!, "logs", "environment"), "*.jsonl");
        Assert.Contains("\"stage\":\"summary\"", await File.ReadAllTextAsync(logs.Order().Last()));
        Assert.Contains("\"status\":\"cancelled\"", await File.ReadAllTextAsync(logs.Order().Last()));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(result.ResumeReportPath!));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(logs.Order().Last()));
        }
    }

    [Fact]
    public async Task Restore_RejectsProtectedLutraDestination()
    {
        using var temp = new TempDirectory();
        var relativeBackup = Path.GetFullPath(Path.Combine(temp.Path, "backups"))
            .TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        var fixture = await CreateFileSetAsync(temp, [FileEntry(relativeBackup + "/nested", "value")]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(fixture.ArtifactPath, new EnvironmentRestoreOptions(root));

        Assert.False(result.Success);
        Assert.Equal("protected_destination", result.ErrorCategory);
    }

    [Fact]
    public async Task Restore_PreflightChecksPrivilegeToolsAndFreeSpace()
    {
        using var temp = new TempDirectory();
        var fileFixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config", "value")]);
        var unprivileged = new FakeHost { IsPrivileged = false };

        var privilege = await new EnvironmentRestoreService(fileFixture.Config, unprivileged)
            .RestoreAsync(fileFixture.ArtifactPath, new EnvironmentRestoreOptions("/"));

        Assert.False(privilege.Success);
        Assert.Equal("privilege_required", privilege.ErrorCategory);

        using var volumeTemp = new TempDirectory();
        var volumeFixture = await CreateVolumeSetAsync(volumeTemp, dockerContainers: []);
        var root = Path.Combine(volumeTemp.Path, "root");
        Directory.CreateDirectory(root);
        var constrained = new FakeHost
        {
            MissingTools = ["docker"],
            AvailableBytesByPath = path => path == root ? 0 : long.MaxValue / 2
        };

        var plan = await new EnvironmentRestoreService(volumeFixture.Config, constrained)
            .RestoreAsync(
                volumeFixture.ArtifactPath,
                new EnvironmentRestoreOptions(root, IncludeVolumes: true));

        Assert.True(plan.Success, plan.ErrorMessage);
        Assert.False(plan.Plan!.CanApply);
        Assert.Contains("docker", plan.Plan.MissingTools);

        var fileRoot = Path.Combine(temp.Path, "space-root");
        Directory.CreateDirectory(fileRoot);
        var noDestinationSpace = new FakeHost
        {
            AvailableBytesByPath = path => path == fileRoot ? 0 : long.MaxValue / 2
        };
        var spacePlan = await new EnvironmentRestoreService(fileFixture.Config, noDestinationSpace)
            .RestoreAsync(fileFixture.ArtifactPath, new EnvironmentRestoreOptions(fileRoot));

        Assert.True(spacePlan.Success, spacePlan.ErrorMessage);
        Assert.False(spacePlan.Plan!.CanApply);
        Assert.True(spacePlan.Plan.DestinationRequiredBytes > spacePlan.Plan.DestinationAvailableBytes);

        var noStagingSpace = await new EnvironmentRestoreService(
                fileFixture.Config, new FakeHost { AvailableBytes = 0 })
            .RestoreAsync(fileFixture.ArtifactPath, new EnvironmentRestoreOptions(fileRoot));

        Assert.False(noStagingSpace.Success);
        Assert.Equal("insufficient_staging_space", noStagingSpace.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fileRoot));
    }

    [Fact]
    public async Task Restore_NoRollbackCopyLeavesNoRollbackArtifact()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config", "new")]);
        var root = Path.Combine(temp.Path, "root");
        var destination = Path.Combine(root, "etc", "app", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old");

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(
                fixture.ArtifactPath,
                new EnvironmentRestoreOptions(root, Apply: true, CreateRollbackCopy: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.RollbackDirectory);
        Assert.Equal("new", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Restore_RejectsDuplicateNormalizedDestination()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [
            FileEntry("etc/app/config", "one"),
            FileEntry("etc/app/config", "two")
        ]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(fixture.ArtifactPath, new EnvironmentRestoreOptions(root));

        Assert.False(result.Success);
        Assert.Equal("duplicate_destination", result.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Restore_RejectsFileDirectoryDestinationConflict()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config", "value")]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(Path.Combine(root, "etc", "app", "config"));

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Equal("destination_conflict", result.ErrorCategory);
    }

    [Fact]
    public async Task Restore_RejectsPlannedFileAncestorConflictBeforeMutation()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [
            FileEntry("etc/app", "file"),
            FileEntry("etc/app/config", "child")
        ]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);

        var result = await new EnvironmentRestoreService(fixture.Config, new FakeHost())
            .RestoreAsync(fixture.ArtifactPath, new EnvironmentRestoreOptions(root, Apply: true));

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Equal("destination_conflict", result.ErrorCategory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Restore_RejectsChangedPlanBeforeMutation()
    {
        using var temp = new TempDirectory();
        var fixture = await CreateFileSetAsync(temp, [FileEntry("etc/app/config", "value")]);
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var service = new EnvironmentRestoreService(fixture.Config, new FakeHost());
        var preflight = await service.RestoreAsync(
            fixture.ArtifactPath, new EnvironmentRestoreOptions(root));
        Directory.CreateDirectory(Path.Combine(root, "etc", "app"));
        await File.WriteAllTextAsync(Path.Combine(root, "etc", "app", "config"), "value");

        var result = await service.RestoreAsync(
            fixture.ArtifactPath,
            new EnvironmentRestoreOptions(
                root,
                Apply: true,
                ExpectedPlanToken: preflight.Plan!.ConfirmationToken));

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Equal("plan_changed", result.ErrorCategory);
        Assert.Equal("value", await File.ReadAllTextAsync(Path.Combine(root, "etc", "app", "config")));
    }

    private static NestedEntry FileEntry(string name, string content, int mode = 420)
        => new(name, TarEntryType.RegularFile, content, mode);

    private static NestedEntry DirectoryEntry(string name, int mode)
        => new(name, TarEntryType.Directory, "", mode);

    private static async Task<RecoveryFixture> CreateFileSetAsync(
        TempDirectory temp,
        IReadOnlyList<NestedEntry> entries,
        IReadOnlyList<string>? systemdUnits = null)
    {
        var payload = Path.Combine(temp.Path, $"payload-{Guid.NewGuid():N}.tar.gz");
        await WriteNestedArchiveAsync(payload, entries);
        return await CreateSetAsync(
            temp, "files", EnvironmentRecoverySourceKind.File, payload,
            systemdUnits ?? [], [], includeVolumeConfig: false);
    }

    private static async Task<RecoveryFixture> CreateVolumeSetAsync(
        TempDirectory temp,
        IReadOnlyList<string> dockerContainers)
    {
        var payload = Path.Combine(temp.Path, $"volume-{Guid.NewGuid():N}.tar.gz");
        await WriteNestedArchiveAsync(payload, [
            DirectoryEntry("./", 493),
            FileEntry("./data.txt", "volume-data")
        ]);
        return await CreateSetAsync(
            temp, "volume", EnvironmentRecoverySourceKind.Volume, payload,
            [], dockerContainers, includeVolumeConfig: true);
    }

    private static async Task<RecoveryFixture> CreateSetAsync(
        TempDirectory temp,
        string sourceName,
        EnvironmentRecoverySourceKind kind,
        string payload,
        IReadOnlyList<string> systemdUnits,
        IReadOnlyList<string> dockerContainers,
        bool includeVolumeConfig)
    {
        var payloadBytes = await File.ReadAllBytesAsync(payload);
        var source = new EnvironmentRecoverySource
        {
            Name = sourceName,
            Kind = kind,
            PayloadPath = $"payload/{(kind == EnvironmentRecoverySourceKind.File ? "files" : "volumes")}/{sourceName}.tar.gz",
            SizeBytes = payloadBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
            RestoreOrder = 0
        };
        var manifest = new EnvironmentRecoveryManifest
        {
            FormatVersion = 1,
            ArtifactId = "fixture",
            CreatedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            LutraVersion = "test",
            Sources = [source],
            RequiredTools = kind == EnvironmentRecoverySourceKind.Volume ? ["docker"] : [],
            SystemdUnits = systemdUnits.ToList(),
            DockerContainers = dockerContainers.ToList()
        };
        var artifactDirectory = Path.Combine(temp.Path, "artifacts", Guid.NewGuid().ToString("N"));
        var artifact = Path.Combine(artifactDirectory, "environment_fixture.tar.gz");
        await EnvironmentRecoveryArchive.WriteAsync(
            artifact, manifest, new Dictionary<string, string> { [sourceName] = payload },
            InventoryRenderer.ToJson(new InventorySnapshot
            {
                CapturedAt = manifest.CreatedAt,
                Host = "fixture-host",
                LutraVersion = "test",
                Sections =
                [
                    new InventorySection
                    {
                        Name = "os",
                        Status = InventoryCollectorStatus.Succeeded,
                        Required = true
                    }
                ]
            }),
            "inventory", "{}", "missing", "restore");
        var sha = await BackupIntegrity.ComputeSha256Async(artifact);
        await BackupIntegrity.WriteChecksumFileAsync(artifact, sha);
        await File.WriteAllTextAsync(artifact + ".json", EnvironmentRecoveryArchive.Serialize(
            new EnvironmentRecoveryDescriptor
            {
                FormatVersion = 1,
                ArtifactId = manifest.ArtifactId,
                CreatedAt = manifest.CreatedAt,
                ArtifactFileName = Path.GetFileName(artifact),
                FileSizeBytes = new FileInfo(artifact).Length,
                Sha256 = sha,
                LutraVersion = "test",
                Sources = [new EnvironmentRecoveryDescriptorSource { Name = sourceName, Kind = kind }],
                Success = true
            }));
        var config = new BackupConfig
        {
            BackupDirectory = Path.Combine(temp.Path, "backups"),
            StateDirectory = Path.Combine(temp.Path, "state"),
            ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
            Retention = new RetentionPolicy(),
            Volumes = includeVolumeConfig
                ? [new VolumeTarget { Name = sourceName, Volume = "app_data", Schedule = "daily" }]
                : []
        };
        return new RecoveryFixture(config, artifact);
    }

    private static async Task WriteNestedArchiveAsync(string path, IReadOnlyList<NestedEntry> entries)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new TarWriter(gzip, leaveOpen: false);
        foreach (var item in entries)
        {
            var entry = new PaxTarEntry(item.Type, item.Name)
            {
                Mode = (UnixFileMode)item.Mode,
                ModificationTime = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)
            };
            if (item.Type == TarEntryType.RegularFile)
                entry.DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Content));
            if (item.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                entry.LinkName = "/tmp/sentinel-secret";
            await writer.WriteEntryAsync(entry);
            entry.DataStream?.Dispose();
        }
    }

    private sealed record NestedEntry(string Name, TarEntryType Type, string Content, int Mode = 420);
    private sealed record RecoveryFixture(BackupConfig Config, string ArtifactPath);

    private sealed class FakeHost : IEnvironmentRestoreHost
    {
        public bool IsPrivileged { get; set; } = true;
        public List<string> Consumers { get; set; } = [];
        public bool FailValidation { get; set; }
        public bool Validated { get; private set; }
        public bool CancelVolumeRestore { get; set; }
        public long AvailableBytes { get; set; } = long.MaxValue / 2;
        public Func<string, long>? AvailableBytesByPath { get; set; }
        public List<string> MissingTools { get; set; } = [];
        public bool Activated { get; private set; }
        public List<string> StoppedContainers { get; } = [];
        public List<string> RestoredVolumes { get; } = [];

        public long GetAvailableBytes(string path) => AvailableBytesByPath?.Invoke(path) ?? AvailableBytes;
        public Task<bool> ToolExistsAsync(string tool, CancellationToken cancellationToken)
            => Task.FromResult(!MissingTools.Contains(tool, StringComparer.Ordinal));
        public Task<IReadOnlyList<string>> GetVolumeConsumersAsync(string volume, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(Consumers);
        public Task StopContainersAsync(IReadOnlyList<string> containers, CancellationToken cancellationToken)
        {
            StoppedContainers.AddRange(containers);
            return Task.CompletedTask;
        }
        public Task StopSystemdUnitsAsync(IReadOnlyList<string> units, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task RestoreVolumeAsync(string volume, string payloadPath, CancellationToken cancellationToken)
        {
            if (CancelVolumeRestore)
                throw new OperationCanceledException(cancellationToken);
            RestoredVolumes.Add(volume);
            return Task.CompletedTask;
        }
        public Task ValidateServicesAsync(string rootPath, IReadOnlyList<string> systemdUnits, CancellationToken cancellationToken)
        {
            Validated = true;
            return FailValidation
                ? throw new InvalidOperationException("sentinel-secret")
                : Task.CompletedTask;
        }
        public Task ActivateServicesAsync(
            IReadOnlyList<string> systemdUnits,
            IReadOnlyList<string> containers,
            CancellationToken cancellationToken)
        {
            Activated = true;
            return Task.CompletedTask;
        }
    }
}
