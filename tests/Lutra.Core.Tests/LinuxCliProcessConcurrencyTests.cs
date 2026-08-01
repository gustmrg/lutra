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
        var sourceA = Path.Combine(temp.Path, "source-a");
        var sourceB = Path.Combine(temp.Path, "source-b");
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var stateDirectory = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        await File.WriteAllTextAsync(Path.Combine(sourceA, "a.txt"), new string('a', 256 * 1024));
        await File.WriteAllTextAsync(Path.Combine(sourceB, "b.txt"), new string('b', 256 * 1024));

        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            state_directory: {{stateDirectory}}
            retention:
              max_count: 20
              max_age_days: 30
            files:
              - name: files-a
                paths: [{{sourceA}}]
                schedule: daily
              - name: files-b
                paths: [{{sourceB}}]
                schedule: daily
            """);

        const int overlapRounds = 5;
        var results = new List<ProcessResult>();
        for (var round = 0; round < overlapRounds; round++)
        {
            var pair = await Task.WhenAll(
                RunCliAsync(configPath, "backup", "run", "--target", "files-a"),
                RunCliAsync(configPath, "backup", "run", "--target", "files-b"));
            results.AddRange(pair);
        }

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.DoesNotContain(
            results,
            result => (result.StandardOutput + result.StandardError)
                .Contains("busy", StringComparison.OrdinalIgnoreCase));

        var database = new LutraDatabase(stateDirectory, configPath, backupDirectory);
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
        var sourcePath = Path.Combine(temp.Path, "source.txt");
        var backupDirectory = Path.Combine(temp.Path, "backups");
        var stateDirectory = Path.Combine(temp.Path, "state");
        await File.WriteAllTextAsync(sourcePath, "source");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(stateDirectory);
        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            state_directory: {{stateDirectory}}
            retention:
              max_count: 3
              max_age_days: 7
            files:
              - name: files
                paths: [{{sourcePath}}]
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
    public async Task CliSmoke_ImportsLegacyJsonAndReportsConcurrentBackupStates()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TempDirectory();
        var sourceA = Path.Combine(temp.Path, "smoke-a.txt");
        var sourceB = Path.Combine(temp.Path, "smoke-b.txt");
        var backupDirectory = Path.Combine(temp.Path, "backups");
        await File.WriteAllTextAsync(sourceA, "smoke-a");
        await File.WriteAllTextAsync(sourceB, "smoke-b");
        Directory.CreateDirectory(backupDirectory);
        var configPath = Path.Combine(temp.Path, "lutra.yaml");
        await File.WriteAllTextAsync(configPath, $$"""
            backup_directory: {{backupDirectory}}
            retention:
              max_count: 3
              max_age_days: 7
            files:
              - name: smoke-a
                paths: [{{sourceA}}]
                schedule: daily
              - name: smoke-b
                paths: [{{sourceB}}]
                schedule: daily
            """);
        var legacyPath = Path.Combine(backupDirectory, "backup-history.json");
        var legacyBytes = """
            [
              {
                "target_name": "legacy-files",
                "timestamp": "2026-08-01T12:00:00Z",
                "file_name": "legacy.tar.gz",
                "file_size_bytes": 42,
                "duration_ms": 10,
                "success": true
              }
            ]
            """u8.ToArray();
        await File.WriteAllBytesAsync(legacyPath, legacyBytes);

        var validation = await RunCliAsync(configPath, "config", "validate");
        Assert.Equal(0, validation.ExitCode);
        Assert.Contains("State directory compatibility fallback", validation.StandardOutput);
        Assert.Contains("writable (SQLite WAL)", validation.StandardOutput);
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyPath));

        var backups = await Task.WhenAll(
            RunCliAsync(configPath, "backup", "run", "--target", "smoke-a"),
            RunCliAsync(configPath, "backup", "run", "--target", "smoke-b"));
        Assert.All(backups, result => Assert.Equal(0, result.ExitCode));

        var history = await RunCliAsync(configPath, "history");
        Assert.Equal(0, history.ExitCode);
        Assert.Contains("legacy-", history.StandardOutput);
        Assert.Contains("smoke-a", history.StandardOutput);
        Assert.Contains("smoke-b", history.StandardOutput);
        Assert.Equal(3, CountOccurrences(history.StandardOutput, "OK"));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyPath));

        var stateDirectory = Path.Combine(backupDirectory, ".lutra-state");
        var records = await new SqliteBackupHistoryRepository(
                new LutraDatabase(stateDirectory, configPath, backupDirectory))
            .GetAllRecordsAsync();
        Assert.Equal(3, records.Count);
        Assert.Contains(records, record => record.TargetName == "legacy-files");

        var health = await RunCliAsync(configPath, "health");
        Assert.Equal(0, health.ExitCode);
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
