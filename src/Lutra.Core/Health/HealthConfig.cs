namespace Lutra.Core.Health;

public class HealthConfig
{
    public int MinSamples { get; init; } = 5;
    public int WindowSize { get; init; } = 10;
    public double SizeDeviationThreshold { get; init; } = 2.0;
    public double DurationDeviationThreshold { get; init; } = 2.0;
    public int FailureStreakWarning { get; init; } = 2;
    public int FailureStreakCritical { get; init; } = 3;
    public double MissedScheduleMultiplier { get; init; } = 1.5;
    public double SizeTrendThresholdPercent { get; init; } = 10.0;
}
