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
        var records = Enumerable.Range(0, 3).Select(index => new BackupRecord
        {
            TargetName = target.Name,
            Timestamp = DateTime.UtcNow.AddHours(-index),
            FileName = "",
            FileSizeBytes = 0,
            DurationMs = 1,
            Success = false,
            ErrorMessage = "failed"
        }).ToList();

        var report = new AnomalyDetector(new HealthConfig()).Analyze(records, target);

        Assert.Equal(OverallStatus.Critical, report.OverallStatus);
        Assert.Contains(report.Findings, finding => finding.Type == FindingType.FailureStreak);
        Assert.Contains(report.Findings, finding => finding.Type == FindingType.NoSuccessfulBackup);
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

    [Fact]
    public void SqlServerProvider_GeneratesDifferentialAndLogCommands()
    {
        var differential = new DatabaseTarget
        {
            Name = "sql-diff", Type = DatabaseType.SqlServer, Container = "sql",
            Database = "app", Username = "sa", SqlServerBackupKind = SqlServerBackupKind.Differential
        };
        var log = new DatabaseTarget
        {
            Name = "sql-log", Type = DatabaseType.SqlServer, Container = "sql",
            Database = "app", Username = "sa", SqlServerBackupKind = SqlServerBackupKind.Log
        };
        var provider = new SqlServerBackupProvider();

        Assert.Contains("DIFFERENTIAL", provider.BuildDumpCommand(differential, "one").Arguments.Last());
        Assert.Contains("BACKUP LOG", provider.BuildDumpCommand(log, "two").Arguments.Last());
        Assert.Equal(".diff.bak", provider.GetFileExtension(differential));
        Assert.Equal(".log.bak", provider.GetFileExtension(log));
    }

    [Fact]
    public void MongoProvider_AddsOplogWithoutDatabaseFilter()
    {
        var target = new DatabaseTarget
        {
            Name = "mongo", Type = DatabaseType.MongoDb, Container = "mongo",
            Database = "app", MongoOplog = true
        };
        var command = new MongoBackupProvider().BuildDumpCommand(target, "id");

        Assert.Contains("--oplog", command.Arguments);
        Assert.DoesNotContain("--db", command.Arguments);
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
}
