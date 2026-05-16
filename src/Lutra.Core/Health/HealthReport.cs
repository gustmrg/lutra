namespace Lutra.Core.Health;

public enum OverallStatus
{
    Healthy,
    Warning,
    Critical
}

public class HealthReport
{
    public required string TargetName { get; init; }
    public required List<HealthFinding> Findings { get; init; }
    public required DateTime AnalyzedAt { get; init; }
    public required int TotalBackupsAnalyzed { get; init; }

    public OverallStatus OverallStatus
    {
        get
        {
            if (Findings.Any(f => f.Severity == Severity.Critical))
                return OverallStatus.Critical;
            if (Findings.Any(f => f.Severity == Severity.Warning))
                return OverallStatus.Warning;
            return OverallStatus.Healthy;
        }
    }
}
