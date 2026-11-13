using System.Diagnostics;
using Lutra.Core.History;
using Lutra.Core.Persistence;

namespace Lutra.Core.Tests;

public sealed class LinuxCliProcessConcurrencyTests
{
    [Fact]
    public async Task SeparateCliProcesses_CompleteOverlappingTargetsWithoutLostHistoryOrBusyErrors()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var stateDirectory = Path.Combine(temp.Path, "state");
        WriteFakeDocker(temp);

        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            state_directory: {{stateDirectory}}
            retention:
              max_count: 20
              max_age_days: 30
            databases:
              - name: db-a
                type: postgresql
                container: postgres
                database: app
                username: postgres
                schedule: daily
              - name: db-b
                type: postgresql
                container: postgres
                database: app
                username: postgres
                schedule: daily
            """);

        const int overlapRounds = 5;
        var results = new List<ProcessResult>();
        for (var round = 0; round < overlapRounds; round++)
        {
            var pair = await Task.WhenAll(
                RunCliAsync(configPath, "backup", "run", "--target", "db-a"),
                RunCliAsync(configPath, "backup", "run", "--target", "db-b"));
            results.AddRange(pair);
        }

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.DoesNotContain(
            results,
            result => (result.StandardOutput + result.StandardError)
                .Contains("busy", StringComparison.OrdinalIgnoreCase));

        var database = new LutraDatabase(stateDirectory, configPath);
        var repository = new SqliteBackupHistoryRepository(database);
        var records = await repository.GetAllRecordsAsync();

        Assert.Equal(overlapRounds * 2, records.Count);
        Assert.Equal(records.Count, records.Select(record => record.Id).Distinct().Count());
        Assert.All(records, record =>
        {
            Assert.Equal(HistoryOperationType.Backup, record.OperationType);
            Assert.Equal(HistoryOperationStatus.Succeeded, record.Status);
            Assert.NotNull(record.CompletedAt);
            Assert.Null(record.LeaseId);
        });
        Assert.Equal("ok", database.CheckIntegrity());
    }

    [Fact]
    public async Task ConfigValidate_ExplainsStateDatabasePermissionFailure()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
            return;

        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var stateDirectory = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(stateDirectory);
        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            state_directory: {{stateDirectory}}
            retention:
              max_count: 3
              max_age_days: 7
            databases:
              - name: files
                type: postgresql
                container: postgres
                database: app
                username: postgres
                schedule: daily
            """);

        File.SetUnixFileMode(
            stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await RunCliAsync(configPath, "config", "validate");

            Assert.Equal(1, result.ExitCode);
            var output = result.StandardOutput + result.StandardError;
            Assert.Contains("Application state is not writable", output);
            Assert.Contains("lutra.db-wal", output);
            Assert.Contains("ownership/permissions", output);
        }
        finally
        {
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task CliSmoke_ReportsConcurrentBackupStates()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        WriteFakeDocker(temp);
        Directory.CreateDirectory(backupDirectory);
        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            retention:
              max_count: 3
              max_age_days: 7
            databases:
              - name: smoke-a
                type: postgresql
                container: postgres
                database: app
                username: postgres
                schedule: daily
              - name: smoke-b
                type: postgresql
                container: postgres
                database: app
                username: postgres
                schedule: daily
            """);
        var validation = await RunCliAsync(configPath, "config", "validate");
        Assert.Equal(0, validation.ExitCode);
        Assert.Contains("State directory compatibility fallback", validation.StandardOutput);
        Assert.Contains("writable (SQLite WAL)", validation.StandardOutput);

        var backups = await Task.WhenAll(
            RunCliAsync(configPath, "backup", "run", "--target", "smoke-a"),
            RunCliAsync(configPath, "backup", "run", "--target", "smoke-b"));
        Assert.All(backups, result => Assert.Equal(0, result.ExitCode));

        var history = await RunCliAsync(configPath, "history");
        Assert.Equal(0, history.ExitCode);
        Assert.Contains("smoke-a", history.StandardOutput);
        Assert.Contains("smoke-b", history.StandardOutput);
        Assert.Equal(2, CountOccurrences(history.StandardOutput, "OK"));

        var stateDirectory = Path.Combine(backupDirectory, ".lutra-state");
        var records = await new SqliteBackupHistoryRepository(
                new LutraDatabase(stateDirectory, configPath))
            .GetAllRecordsAsync();
        Assert.Equal(2, records.Count);
    }

    private static async Task<ProcessResult> RunCliAsync(string configPath, params string[] arguments)
    {
        var cliPath = FindCliAssembly();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["PATH"] =
            FakeDockerDirectory(Path.GetDirectoryName(configPath)!) + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Lutra CLI process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string FakeDockerDirectory(string root) => Path.Combine(root, "fake-bin");

    // Stands in for `docker exec <container> pg_dump ...` so the CLI can run a
    // full backup without a database. The dump is large enough for two
    // processes to overlap.
    private static void WriteFakeDocker(TempDirectory temp)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = FakeDockerDirectory(temp.Path);
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "docker");
        File.WriteAllText(script, "#!/bin/sh\nhead -c 262144 /dev/zero\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string FindCliAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Release";
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lutra.slnx")))
            directory = directory.Parent;

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
        var cliPath = Path.Combine(
            root,
            "src",
            "Lutra.CLI",
            "bin",
            configuration,
            "net10.0",
            "Lutra.CLI.dll");
        Assert.True(File.Exists(cliPath), $"CLI assembly was not built at '{cliPath}'.");
        return cliPath;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
