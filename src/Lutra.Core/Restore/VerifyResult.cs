namespace Lutra.Core.Restore;

public record VerifyResult
{
    public required string TargetName { get; init; }
    public required string BackupFilePath { get; init; }
    public required bool ChecksumValid { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? ValidationDetails { get; init; }
    public string? ErrorMessage { get; init; }
}
