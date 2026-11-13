using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Tests;

public sealed class PostgresProviderTests
{
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
}
