namespace Lutra.Core.Health;

public enum FindingType
{
    SizeAnomaly,
    DurationAnomaly,
    FailureStreak,
    MissedSchedule,
    SizeTrend,
    ZeroSize,
    NoSuccessfulBackup,
    MissingFile,
    IntegrityFailure
}

public enum Severity
{
    Info,
    Warning,
    Critical
}

public class HealthFinding
{
    public required FindingType Type { get; init; }
    public required Severity Severity { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
    public DateTime? RelevantTimestamp { get; init; }
}
