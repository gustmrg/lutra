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

    public HealthReport Analyze(IReadOnlyList<HistoryRecord> records, IBackupTarget target)
    {
        var findings = new List<HealthFinding>();

        var terminalRecords = records.Where(record => record.Status.IsTerminal()).ToList();
        if (terminalRecords.Count == 0)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.NoSuccessfulBackup,
                Severity = Severity.Warning,
                Message = "No backup history found for this target."
            });

            return BuildReport(target.Name, findings, 0);
        }

        var orderedRecords = terminalRecords.OrderByDescending(r => r.StartedAt).ToList();
        var successRecords = orderedRecords
            .Where(r => r.Status == HistoryOperationStatus.Succeeded)
            .ToList();

        findings.AddRange(DetectFailureStreaks(orderedRecords));
        findings.AddRange(DetectZeroSizeBackups(successRecords));

        if (successRecords.Count == 0)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.NoSuccessfulBackup,
                Severity = Severity.Critical,
                Message = "This target has no successful backup."
            });
        }

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
        if (intervalHours.HasValue && successRecords.Count >= 1)
            findings.AddRange(DetectMissedSchedules(successRecords, intervalHours.Value));

        return BuildReport(target.Name, findings, terminalRecords.Count);
    }

    private List<HealthFinding> DetectSizeAnomalies(IReadOnlyList<HistoryRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < 2)
            return findings;

        var latest = window[0];
        var latestSize = latest.FileSizeBytes ?? 0;
        var previous = window.Skip(1).Select(r => (double)(r.FileSizeBytes ?? 0)).ToList();
        var mean = StatisticsHelper.Mean(previous);
        var stdDev = StatisticsHelper.StandardDeviation(previous);

        if (stdDev > 0)
        {
            var zScore = Math.Abs(latestSize - mean) / stdDev;
            if (zScore >= _config.SizeDeviationThreshold)
            {
                var pctChange = StatisticsHelper.PercentChange(mean, latestSize);
                findings.Add(new HealthFinding
                {
                    Type = FindingType.SizeAnomaly,
                    Severity = Math.Abs(pctChange) > 80 ? Severity.Critical : Severity.Warning,
                    Message = $"Backup size is {pctChange:+0.0;-0.0}% compared to the recent average.",
                    Detail = $"Expected ~{FormatBytes((long)mean)}, got {FormatBytes(latestSize)} (z-score: {zScore:F1})",
                    RelevantTimestamp = latest.StartedAt.UtcDateTime
                });
            }
        }
        else if (latestSize != (long)mean && mean > 0)
        {
            var pctChange = StatisticsHelper.PercentChange(mean, latestSize);
            if (Math.Abs(pctChange) > 50)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.SizeAnomaly,
                    Severity = Severity.Warning,
                    Message = $"Backup size changed {pctChange:+0.0;-0.0}% from the consistent previous size.",
                    Detail = $"Previous: {FormatBytes((long)mean)}, Latest: {FormatBytes(latestSize)}",
                    RelevantTimestamp = latest.StartedAt.UtcDateTime
                });
            }
        }

        return findings;
    }

    private List<HealthFinding> DetectDurationAnomalies(IReadOnlyList<HistoryRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < 2)
            return findings;

        var latest = window[0];
        var latestDuration = latest.DurationMs ?? 0;
        var previous = window.Skip(1).Select(r => (double)(r.DurationMs ?? 0)).ToList();
        var mean = StatisticsHelper.Mean(previous);
        var stdDev = StatisticsHelper.StandardDeviation(previous);

        if (stdDev <= 0)
            return findings;

        var zScore = Math.Abs(latestDuration - mean) / stdDev;
        if (zScore >= _config.DurationDeviationThreshold)
        {
            var pctChange = StatisticsHelper.PercentChange(mean, latestDuration);
            findings.Add(new HealthFinding
            {
                Type = FindingType.DurationAnomaly,
                Severity = Severity.Warning,
                Message = $"Backup duration is {pctChange:+0.0;-0.0}% compared to the recent average.",
                Detail = $"Expected ~{FormatDuration((long)mean)}, took {FormatDuration(latestDuration)} (z-score: {zScore:F1})",
                RelevantTimestamp = latest.StartedAt.UtcDateTime
            });
        }

        return findings;
    }

    private List<HealthFinding> DetectFailureStreaks(IReadOnlyList<HistoryRecord> orderedRecords)
    {
        var findings = new List<HealthFinding>();

        var consecutiveFailures = 0;
        foreach (var record in orderedRecords)
        {
            if (record.Status != HistoryOperationStatus.Succeeded)
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
                Detail = orderedRecords.First(r => r.Status != HistoryOperationStatus.Succeeded).ErrorMessage,
                RelevantTimestamp = orderedRecords[0].StartedAt.UtcDateTime
            });
        }
        else if (consecutiveFailures >= _config.FailureStreakWarning)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.FailureStreak,
                Severity = Severity.Warning,
                Message = $"{consecutiveFailures} consecutive backup failures.",
                Detail = orderedRecords.First(r => r.Status != HistoryOperationStatus.Succeeded).ErrorMessage,
                RelevantTimestamp = orderedRecords[0].StartedAt.UtcDateTime
            });
        }

        return findings;
    }

    private List<HealthFinding> DetectMissedSchedules(
        IReadOnlyList<HistoryRecord> successRecords, double expectedIntervalHours)
    {
        var findings = new List<HealthFinding>();

        var latest = successRecords[0];
        var hoursSinceLastBackup = (DateTimeOffset.UtcNow - latest.StartedAt).TotalHours;
        var threshold = expectedIntervalHours * _config.MissedScheduleMultiplier;

        if (hoursSinceLastBackup > threshold)
        {
            findings.Add(new HealthFinding
            {
                Type = FindingType.MissedSchedule,
                Severity = Severity.Warning,
                Message = $"No successful backup in {hoursSinceLastBackup:F0} hours (expected every {expectedIntervalHours:F0}h).",
                Detail = $"Last successful backup: {latest.StartedAt:yyyy-MM-dd HH:mm:ss} UTC",
                RelevantTimestamp = latest.StartedAt.UtcDateTime
            });
        }

        for (var i = 0; i < successRecords.Count - 1; i++)
        {
            var gap = (successRecords[i].StartedAt - successRecords[i + 1].StartedAt).TotalHours;
            if (gap > threshold)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.MissedSchedule,
                    Severity = Severity.Info,
                    Message = $"Gap of {gap:F0} hours detected between backups.",
                    Detail = $"Between {successRecords[i + 1].StartedAt:yyyy-MM-dd HH:mm} and {successRecords[i].StartedAt:yyyy-MM-dd HH:mm} UTC",
                    RelevantTimestamp = successRecords[i].StartedAt.UtcDateTime
                });
            }
        }

        return findings;
    }

    private List<HealthFinding> DetectSizeTrend(IReadOnlyList<HistoryRecord> window)
    {
        var findings = new List<HealthFinding>();
        if (window.Count < _config.MinSamples)
            return findings;

        // Oldest first for regression
        var sizes = window.Select(r => (double)(r.FileSizeBytes ?? 0)).Reverse().ToList();
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
                Detail = $"Oldest: {FormatBytes(window[^1].FileSizeBytes ?? 0)}, Latest: {FormatBytes(window[0].FileSizeBytes ?? 0)} over {window.Count} backups"
            });
        }

        return findings;
    }

    private static List<HealthFinding> DetectZeroSizeBackups(IReadOnlyList<HistoryRecord> successRecords)
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
                RelevantTimestamp = record.StartedAt.UtcDateTime
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
