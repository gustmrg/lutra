namespace Lutra.Core.Restore;

public record RestoreResult
{
    public required string TargetName { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? DestinationDatabase { get; init; }
    public string? ErrorMessage { get; init; }
}
