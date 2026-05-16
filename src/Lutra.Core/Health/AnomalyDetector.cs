using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Health;

public class AnomalyDetector
{
    private readonly HealthConfig _config;

    public AnomalyDetector(HealthConfig config)
    {
        _config = config;
    }

    public HealthReport Analyze(IReadOnlyList<BackupRecord> records, DatabaseTarget target)
    {
        var findings = new List<HealthFinding>();

        if (records.Count == 0)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.FailureStreak,
                Severity = Severity.Info,
                Message = "No backup history found for this target."
            });

            return BuildReport(target.Name, findings, 0);
        }

        var orderedRecords = records.OrderByDescending(r => r.Timestamp).ToList();
        var successRecords = orderedRecords.Where(r => r.Success).ToList();

        findings.AddRange(DetectFailureStreaks(orderedRecords));
        findings.AddRange(DetectZeroSizeBackups(successRecords));

        if (successRecords.Count >= _config.MinSamples)
        {
            var window = successRecords.Take(_config.WindowSize).ToList();
            findings.AddRange(DetectSizeAnomalies(window));
            findings.AddRange(DetectDurationAnomalies(window));
            findings.AddRange(DetectSizeTrend(window));
        }
        else if (successRecords.Count > 0)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.SizeAnomaly,
                Severity = Severity.Info,
                Message = $"Insufficient history for statistical analysis ({successRecords.Count}/{_config.MinSamples} minimum)."
            });
        }

        var intervalHours = ScheduleIntervalEstimator.EstimateIntervalHours(target.Schedule);
        if (intervalHours.HasValue && successRecords.Count >= 2)
            findings.AddRange(DetectMissedSchedules(successRecords, intervalHours.Value));

        return BuildReport(target.Name, findings, records.Count);
    }

    private List<HealthFinding> DetectSizeAnomalies(IReadOnlyList<BackupRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < 2)
            return findings;

        var latest = window[0];
        var previous = window.Skip(1).Select(r => (double)r.FileSizeBytes).ToList();
        var mean = StatisticsHelper.Mean(previous);
        var stdDev = StatisticsHelper.StandardDeviation(previous);

        if (stdDev > 0)
        {
            var zScore = Math.Abs(latest.FileSizeBytes - mean) / stdDev;
            if (zScore >= _config.SizeDeviationThreshold)
            {
                var pctChange = StatisticsHelper.PercentChange(mean, latest.FileSizeBytes);
                findings.Add(new HealthFinding
                {
                    Type = FindingType.SizeAnomaly,
                    Severity = Math.Abs(pctChange) > 80 ? Severity.Critical : Severity.Warning,
                    Message = $"Backup size is {pctChange:+0.0;-0.0}% compared to the recent average.",
                    Detail = $"Expected ~{FormatBytes((long)mean)}, got {FormatBytes(latest.FileSizeBytes)} (z-score: {zScore:F1})",
                    RelevantTimestamp = latest.Timestamp
                });
            }
        }
        else if (latest.FileSizeBytes != (long)mean && mean > 0)
        {
            var pctChange = StatisticsHelper.PercentChange(mean, latest.FileSizeBytes);
            if (Math.Abs(pctChange) > 50)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.SizeAnomaly,
                    Severity = Severity.Warning,
                    Message = $"Backup size changed {pctChange:+0.0;-0.0}% from the consistent previous size.",
                    Detail = $"Previous: {FormatBytes((long)mean)}, Latest: {FormatBytes(latest.FileSizeBytes)}",
                    RelevantTimestamp = latest.Timestamp
                });
            }
        }

        return findings;
    }

    private List<HealthFinding> DetectDurationAnomalies(IReadOnlyList<BackupRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < 2)
            return findings;

        var latest = window[0];
        var previous = window.Skip(1).Select(r => (double)r.DurationMs).ToList();
        var mean = StatisticsHelper.Mean(previous);
        var stdDev = StatisticsHelper.StandardDeviation(previous);

        if (stdDev <= 0)
            return findings;

        var zScore = Math.Abs(latest.DurationMs - mean) / stdDev;
        if (zScore >= _config.DurationDeviationThreshold)
        {
            var pctChange = StatisticsHelper.PercentChange(mean, latest.DurationMs);
            findings.Add(new HealthFinding
            {
                Type = FindingType.DurationAnomaly,
                Severity = Severity.Warning,
                Message = $"Backup duration is {pctChange:+0.0;-0.0}% compared to the recent average.",
                Detail = $"Expected ~{FormatDuration((long)mean)}, took {FormatDuration(latest.DurationMs)} (z-score: {zScore:F1})",
                RelevantTimestamp = latest.Timestamp
            });
        }

        return findings;
    }

    private List<HealthFinding> DetectFailureStreaks(IReadOnlyList<BackupRecord> orderedRecords)
    {
        var findings = new List<HealthFinding>();

        var consecutiveFailures = 0;
        foreach (var record in orderedRecords)
        {
            if (!record.Success)
                consecutiveFailures++;
            else
                break;
        }

        if (consecutiveFailures >= _config.FailureStreakCritical)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.FailureStreak,
                Severity = Severity.Critical,
                Message = $"{consecutiveFailures} consecutive backup failures.",
                Detail = orderedRecords.First(r => !r.Success).ErrorMessage,
                RelevantTimestamp = orderedRecords[0].Timestamp
            });
        }
        else if (consecutiveFailures >= _config.FailureStreakWarning)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.FailureStreak,
                Severity = Severity.Warning,
                Message = $"{consecutiveFailures} consecutive backup failures.",
                Detail = orderedRecords.First(r => !r.Success).ErrorMessage,
                RelevantTimestamp = orderedRecords[0].Timestamp
            });
        }

        return findings;
    }

    private List<HealthFinding> DetectMissedSchedules(
        IReadOnlyList<BackupRecord> successRecords, double expectedIntervalHours)
    {
        var findings = new List<HealthFinding>();

        var latest = successRecords[0];
        var hoursSinceLastBackup = (DateTime.UtcNow - latest.Timestamp).TotalHours;
        var threshold = expectedIntervalHours * _config.MissedScheduleMultiplier;

        if (hoursSinceLastBackup > threshold)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.MissedSchedule,
                Severity = Severity.Warning,
                Message = $"No successful backup in {hoursSinceLastBackup:F0} hours (expected every {expectedIntervalHours:F0}h).",
                Detail = $"Last successful backup: {latest.Timestamp:yyyy-MM-dd HH:mm:ss} UTC",
                RelevantTimestamp = latest.Timestamp
            });
        }

        for (var i = 0; i < successRecords.Count - 1; i++)
        {
            var gap = (successRecords[i].Timestamp - successRecords[i + 1].Timestamp).TotalHours;
            if (gap > threshold)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.MissedSchedule,
                    Severity = Severity.Info,
                    Message = $"Gap of {gap:F0} hours detected between backups.",
                    Detail = $"Between {successRecords[i + 1].Timestamp:yyyy-MM-dd HH:mm} and {successRecords[i].Timestamp:yyyy-MM-dd HH:mm} UTC",
                    RelevantTimestamp = successRecords[i].Timestamp
                });
            }
        }

        return findings;
    }

    private List<HealthFinding> DetectSizeTrend(IReadOnlyList<BackupRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < _config.MinSamples)
            return findings;

        // Oldest first for regression
        var sizes = window.Select(r => (double)r.FileSizeBytes).Reverse().ToList();
        var slope = StatisticsHelper.LinearRegressionSlope(sizes);
        var mean = StatisticsHelper.Mean(sizes);

        if (mean == 0)
            return findings;

        var slopePercentPerBackup = slope / mean * 100;

        if (Math.Abs(slopePercentPerBackup) > _config.SizeTrendThresholdPercent)
        {
            var direction = slopePercentPerBackup > 0 ? "growing" : "shrinking";
            findings.Add(new HealthFinding
            {
                Type = FindingType.SizeTrend,
                Severity = Severity.Info,
                Message = $"Backup size is consistently {direction} ({slopePercentPerBackup:+0.0;-0.0}% per backup).",
                Detail = $"Oldest: {FormatBytes(window[^1].FileSizeBytes)}, Latest: {FormatBytes(window[0].FileSizeBytes)} over {window.Count} backups"
            });
        }

        return findings;
    }

    private static List<HealthFinding> DetectZeroSizeBackups(IReadOnlyList<BackupRecord> successRecords)
    {
        var findings = new List<HealthFinding>();

        foreach (var record in successRecords.Where(r => r.FileSizeBytes == 0))
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.ZeroSize,
                Severity = Severity.Critical,
                Message = "Backup reported success but file size is 0 bytes.",
                Detail = $"File: {record.FileName}",
                RelevantTimestamp = record.Timestamp
            });
        }

        return findings;
    }

    private static HealthReport BuildReport(string targetName, List<HealthFinding> findings, int totalAnalyzed)
    {
        return new HealthReport
        {
            TargetName = targetName,
            Findings = findings,
            AnalyzedAt = DateTime.UtcNow,
            TotalBackupsAnalyzed = totalAnalyzed
        };
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    private static string FormatDuration(long ms)
    {
        return ms switch
        {
            >= 60_000 => $"{ms / 60_000.0:F1} min",
            >= 1000 => $"{ms / 1000.0:F1}s",
            _ => $"{ms}ms"
        };
    }
}
