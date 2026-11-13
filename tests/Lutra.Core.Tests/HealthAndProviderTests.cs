using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Health;
using Lutra.Core.History;

namespace Lutra.Core.Tests;

public sealed class HealthAndProviderTests
{
    [Fact]
    public void Analyze_FlagsFailureStreakAndMissingSuccess()
    {
        var target = PostgreSqlTarget();
        var records = Enumerable.Range(0, 3).Select(index =>
        {
            var startedAt = DateTimeOffset.UtcNow.AddHours(-index);
            return new HistoryRecord
            {
                TargetName = target.Name,
                OperationType = HistoryOperationType.Backup,
                Status = HistoryOperationStatus.Failed,
                StartedAt = startedAt,
                UpdatedAt = startedAt.AddMilliseconds(1),
                CompletedAt = startedAt.AddMilliseconds(1),
                FileSizeBytes = 0,
                DurationMs = 1,
                ErrorMessage = "failed"
            };
        }).ToList();

        var report = new AnomalyDetector(new HealthConfig()).Analyze(records, target);

        Assert.Equal(OverallStatus.Critical, report.OverallStatus);
        Assert.Contains(report.Findings, finding => finding.Type == FindingType.FailureStreak);
        Assert.Contains(report.Findings, finding => finding.Type == FindingType.NoSuccessfulBackup);
    }

    [Fact]
    public void Analyze_ExcludesActiveRowsAndCountsInterruptedAsFailure()
    {
        var target = PostgreSqlTarget();
        var now = DateTimeOffset.UtcNow;
        var records = new List<HistoryRecord>
        {
            Operation(HistoryOperationStatus.Creating, now),
            Operation(HistoryOperationStatus.Interrupted, now.AddMinutes(-1)),
            Operation(HistoryOperationStatus.Cancelled, now.AddMinutes(-2)),
            Operation(HistoryOperationStatus.Failed, now.AddMinutes(-3))
        };

        var report = new AnomalyDetector(new HealthConfig()).Analyze(records, target);

        Assert.Equal(3, report.TotalBackupsAnalyzed);
        Assert.Contains(report.Findings, finding => finding.Type == FindingType.FailureStreak);
    }

    [Fact]
    public void PostgresProvider_UsesEnvironmentForPasswordAndExpectedFormat()
    {
        const string variable = "LUTRA_TEST_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "secret");
        try
        {
            var target = PostgreSqlTarget(variable);
            var command = new PostgresBackupProvider().BuildDumpCommand(target, "id");

            Assert.Equal("pg_dump", command.Command);
            Assert.Contains("-Fc", command.Arguments);
            Assert.DoesNotContain("secret", command.Arguments);
            Assert.Equal("secret", command.EnvironmentVariables!["PGPASSWORD"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static DatabaseTarget PostgreSqlTarget(string? passwordEnv = null) => new()
    {
        Name = "postgres",
        Type = DatabaseType.PostgreSql,
        Container = "postgres",
        Database = "app",
        Username = "postgres",
        PasswordEnv = passwordEnv,
        Format = "custom",
        Schedule = "daily"
    };

    private static HistoryRecord Operation(HistoryOperationStatus status, DateTimeOffset startedAt)
        => new()
        {
            TargetName = "postgres",
            OperationType = HistoryOperationType.Backup,
            Status = status,
            StartedAt = startedAt,
            UpdatedAt = startedAt,
            CompletedAt = status.IsTerminal() ? startedAt : null,
            LeaseId = status.IsActive() ? Guid.NewGuid() : null,
            LeaseExpiresAt = status.IsActive() ? startedAt.AddMinutes(5) : null,
            ErrorMessage = status.IsTerminal() ? "failed" : null
        };
}
