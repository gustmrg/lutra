using System.Text.Json.Serialization;
using Lutra.Core.Inventory;

namespace Lutra.Core.Recovery;

public sealed record EnvironmentRestoreOptions(
    string RootPath,
    bool Apply = false,
    bool IncludeVolumes = false,
    bool ActivateServices = false,
    bool CreateRollbackCopy = true,
    string? ExpectedPlanToken = null);

public sealed class EnvironmentInspectResult
{
    public required bool Success { get; init; }
    public required string ArtifactPath { get; init; }
    public bool Plaintext { get; init; } = true;
    public bool ChecksumValid { get; init; }
    public EnvironmentRecoveryDescriptor? Descriptor { get; init; }
    public EnvironmentRecoveryManifest? Manifest { get; init; }
    public InventorySnapshot? Inventory { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class EnvironmentRestorePlan
{
    public required string ArtifactPath { get; init; }
    public required string RootPath { get; init; }
    public required string ArtifactId { get; init; }
    public required string ArtifactSha256 { get; init; }
    public required List<EnvironmentRestoreAction> Actions { get; init; }
    public required long RequiredBytes { get; init; }
    public required long AvailableBytes { get; init; }
    public required long StagingRequiredBytes { get; init; }
    public required long StagingAvailableBytes { get; init; }
    public required long DestinationRequiredBytes { get; init; }
    public required long DestinationAvailableBytes { get; init; }
    public required List<string> MissingTools { get; init; }
    public required List<string> Warnings { get; init; }
    public string ConfirmationToken { get; set; } = "";
    public bool CanApply { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action_type")]
[JsonDerivedType(typeof(EnvironmentFileRestoreAction), "file")]
[JsonDerivedType(typeof(EnvironmentDirectoryRestoreAction), "directory")]
[JsonDerivedType(typeof(EnvironmentVolumeRestoreAction), "volume")]
[JsonDerivedType(typeof(EnvironmentServiceRestoreAction), "service")]
public abstract class EnvironmentRestoreAction
{
    public required int Order { get; init; }
    public required string SourceName { get; init; }
    public required string Destination { get; init; }
    public required EnvironmentRestoreActionState State { get; set; }
}

public sealed class EnvironmentFileRestoreAction : EnvironmentRestoreAction
{
    public required string EntryName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required int Mode { get; init; }
    public required DateTimeOffset ModificationTime { get; init; }
}

public sealed class EnvironmentDirectoryRestoreAction : EnvironmentRestoreAction
{
    public required string EntryName { get; init; }
    public required int Mode { get; init; }
    public required DateTimeOffset ModificationTime { get; init; }
}

public sealed class EnvironmentVolumeRestoreAction : EnvironmentRestoreAction
{
    public required string VolumeName { get; init; }
    public required string PayloadPath { get; init; }
    public required List<string> Consumers { get; init; }
}

public sealed class EnvironmentServiceRestoreAction : EnvironmentRestoreAction
{
    public required EnvironmentServiceKind Kind { get; init; }
    public required string Name { get; init; }
}

public enum EnvironmentRestoreActionState
{
    Planned,
    Changed,
    Unchanged,
    Skipped,
    RolledBack,
    Failed
}

public enum EnvironmentServiceKind
{
    Systemd,
    Docker
}

public sealed class EnvironmentRestoreResult
{
    public required bool Success { get; init; }
    public required bool Applied { get; init; }
    public required bool Cancelled { get; init; }
    public EnvironmentRestorePlan? Plan { get; init; }
    public string? RollbackDirectory { get; init; }
    public string? ResumeReportPath { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
    public required TimeSpan Duration { get; init; }
}
