using System.Reflection;
using System.Text;
using System.Text.Json;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Inventory;

/// <summary>
/// Creates best-effort, secret-conscious snapshots of host state for disaster recovery.
/// Collector failures are written into the snapshot and do not fail the snapshot.
/// </summary>
public sealed class InventoryService
{
    private static readonly string[] AllCollectors =
        ["docker", "packages", "systemd", "crontabs", "firewall"];

    private readonly BackupConfig _config;

    public InventoryService(BackupConfig config)
    {
        _config = config;
    }

    public async Task<InventoryResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_config.Inventory is not { Enabled: true } inventory)
            return new InventoryResult(false, null, "Inventory snapshots are not enabled.");

        var startedAt = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("N")[..12];
        var directory = Path.Combine(_config.BackupDirectory, "inventory");
        var fileName = $"inventory_{startedAt:yyyy-MM-dd}_{startedAt:HHmmss}_{id}.md";
        var finalPath = Path.Combine(directory, fileName);
        var tempPath = Path.Combine(directory, $".{fileName}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, "inventory", "Inventory");

            var document = new StringBuilder();
            document.AppendLine("# Lutra Server Inventory");
            document.AppendLine();
            document.AppendLine($"- Captured (UTC): `{startedAt:O}`");
            document.AppendLine($"- Host: `{Environment.MachineName}`");
            document.AppendLine($"- Lutra version: `{GetVersion()}`");
            document.AppendLine();
            document.AppendLine("> This is a restoration aid, not a backup of system state. Secret values and cron commands are intentionally omitted.");

            var collectors = inventory.Collectors ?? AllCollectors.ToList();
            foreach (var collector in collectors.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.AppendLine();
                document.AppendLine(await CollectAsync(collector.ToLowerInvariant(), cancellationToken));
            }

            await File.WriteAllTextAsync(tempPath, document.ToString(), cancellationToken);
            File.Move(tempPath, finalPath);

            var checksum = await BackupIntegrity.ComputeSha256Async(finalPath, cancellationToken);
            await BackupIntegrity.WriteChecksumFileAsync(finalPath, checksum, cancellationToken);
            ApplyRetention(directory, _config.Retention);

            return new InventoryResult(true, finalPath, null);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            return new InventoryResult(false, null, ex.Message);
        }
    }

    private static async Task<string> CollectAsync(string collector, CancellationToken cancellationToken)
    {
        return collector switch
        {
            "docker" => await CollectDockerAsync(cancellationToken),
            "packages" => await CollectPackagesAsync(cancellationToken),
            "systemd" => await CollectSystemdAsync(cancellationToken),
            "crontabs" => await CollectCrontabsAsync(cancellationToken),
            "firewall" => await CollectCommandAsync("Firewall", "ufw", ["status", "verbose"], cancellationToken),
            _ => $"## {collector}\n\n_Not collected: unknown collector._"
        };
    }

    private static async Task<string> CollectDockerAsync(CancellationToken cancellationToken)
    {
        var section = new StringBuilder("## Docker\n\n");
        var ps = await HostProcess.RunAsync("docker",
            ["ps", "--all", "--format", "table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"], cancellationToken);
        AppendResult(section, "Containers", ps);

        if (ps.ExitCode == -1)
            return section.ToString().TrimEnd();

        // ArgumentList deliberately does not invoke a shell, so obtain IDs separately.
        var ids = await HostProcess.RunAsync("docker", ["ps", "-aq"], cancellationToken);
        if (ids.IsSuccess)
        {
            var containerIds = ids.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (containerIds.Length > 0)
            {
                var inspect = await HostProcess.RunAsync("docker", ["inspect", .. containerIds], cancellationToken);
                AppendDockerInspect(section, inspect);
            }
            else
            {
                section.AppendLine("### Container configuration\n\n_No containers found._\n");
            }
        }
        else
        {
            AppendResult(section, "Container configuration", ids);
        }

        var networks = await HostProcess.RunAsync("docker",
            ["network", "ls", "--format", "table {{.Name}}\t{{.Driver}}\t{{.Scope}}"], cancellationToken);
        AppendResult(section, "Networks", networks);
        var volumes = await HostProcess.RunAsync("docker",
            ["volume", "ls", "--format", "table {{.Name}}\t{{.Driver}}"], cancellationToken);
        AppendResult(section, "Volumes", volumes);
        return section.ToString().TrimEnd();
    }

    private static void AppendDockerInspect(StringBuilder section, HostProcessResult result)
    {
        section.AppendLine("### Container configuration");
        section.AppendLine();
        if (!result.IsSuccess)
        {
            section.AppendLine($"_Unavailable: {SafeError(result)}_");
            return;
        }

        try
        {
            using var json = JsonDocument.Parse(result.StdOut);
            foreach (var container in json.RootElement.EnumerateArray())
            {
                var name = GetString(container, "Name")?.TrimStart('/') ?? "unknown";
                var config = container.GetProperty("Config");
                section.AppendLine($"#### {name}");
                section.AppendLine($"- Image: `{GetString(config, "Image") ?? "unknown"}`");

                if (config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
                {
                    var names = env.EnumerateArray()
                        .Select(e => e.GetString()?.Split('=', 2)[0])
                        .Where(e => !string.IsNullOrWhiteSpace(e));
                    section.AppendLine($"- Environment variable names: {string.Join(", ", names.Select(n => $"`{n}`"))}");
                }

                if (container.TryGetProperty("Mounts", out var mounts))
                {
                    var values = mounts.EnumerateArray().Select(m =>
                        $"{GetString(m, "Type")}:{GetString(m, "Source")} -> {GetString(m, "Destination")}");
                    section.AppendLine($"- Mounts: {string.Join("; ", values)}");
                }

                if (container.TryGetProperty("NetworkSettings", out var networkSettings)
                    && networkSettings.TryGetProperty("Networks", out var networks))
                    section.AppendLine($"- Networks: {string.Join(", ", networks.EnumerateObject().Select(p => p.Name))}");

                section.AppendLine();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            section.AppendLine($"_Could not summarize docker inspect output: {ex.Message}_");
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> CollectPackagesAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in new[]
                 {
                     ("dpkg-query", new[] { "-W", "-f=${binary:Package} ${Version}\\n" }),
                     ("rpm", new[] { "-qa" }),
                     ("apk", new[] { "info", "-vv" })
                 })
        {
            var result = await HostProcess.RunAsync(candidate.Item1, candidate.Item2, cancellationToken);
            if (result.ExitCode != -1)
                return FormatResult("Installed packages", result);
        }

        return "## Installed packages\n\n_Unavailable: no supported package manager found._";
    }

    private static async Task<string> CollectSystemdAsync(CancellationToken cancellationToken)
    {
        var section = new StringBuilder("## Systemd\n\n");
        AppendResult(section, "Enabled units", await HostProcess.RunAsync("systemctl",
            ["list-unit-files", "--state=enabled", "--no-pager", "--no-legend"], cancellationToken));
        AppendResult(section, "Running services", await HostProcess.RunAsync("systemctl",
            ["list-units", "--type=service", "--state=running", "--no-pager", "--no-legend"], cancellationToken));
        return section.ToString().TrimEnd();
    }

    private static async Task<string> CollectCrontabsAsync(CancellationToken cancellationToken)
    {
        var section = new StringBuilder("## User crontabs\n\n");
        section.AppendLine("Commands are omitted because cron entries commonly contain credentials.");

        var users = new List<string> { Environment.UserName };
        if (Environment.IsPrivilegedProcess)
        {
            var passwd = await HostProcess.RunAsync("getent", ["passwd"], cancellationToken);
            if (passwd.IsSuccess)
            {
                users = passwd.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split(':'))
                    .Where(fields => fields.Length >= 7
                        && int.TryParse(fields[2], out var uid)
                        && (uid == 0 || uid >= 1000)
                        && !fields[6].Contains("nologin", StringComparison.OrdinalIgnoreCase)
                        && !fields[6].EndsWith("/false", StringComparison.OrdinalIgnoreCase))
                    .Select(fields => fields[0])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
        }

        var found = false;
        foreach (var user in users)
        {
            var args = Environment.IsPrivilegedProcess
                ? new[] { "-l", "-u", user }
                : new[] { "-l" };
            var result = await HostProcess.RunAsync("crontab", args, cancellationToken);
            if (result.ExitCode == -1)
            {
                section.AppendLine("\n_Unavailable: crontab is not installed._");
                return section.ToString().TrimEnd();
            }
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
                continue;

            found = true;
            section.AppendLine();
            section.AppendLine($"### {user}");
            section.AppendLine();
            AppendSanitizedCrontab(section, result.StdOut);
        }

        if (!found)
            section.AppendLine("\n_No readable user crontabs found._");
        return section.ToString().TrimEnd();
    }

    private static void AppendSanitizedCrontab(StringBuilder section, string crontab)
    {
        section.AppendLine("```text");
        foreach (var rawLine in crontab.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('@'))
            {
                var firstSpace = line.IndexOf(' ');
                section.AppendLine(firstSpace > 0 ? $"{line[..firstSpace]} [command omitted]" : line);
                continue;
            }

            var fields = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 5)
                section.AppendLine($"{string.Join(' ', fields.Take(5))} [command omitted]");
        }
        section.AppendLine("```");
    }

    private static async Task<string> CollectCommandAsync(
        string heading, string command, IReadOnlyList<string> args, CancellationToken cancellationToken)
        => FormatResult(heading, await HostProcess.RunAsync(command, args, cancellationToken));

    private static string FormatResult(string heading, HostProcessResult result)
    {
        var section = new StringBuilder($"## {heading}\n\n");
        AppendOutput(section, result);
        return section.ToString().TrimEnd();
    }

    private static void AppendResult(StringBuilder section, string heading, HostProcessResult result)
    {
        section.AppendLine($"### {heading}");
        section.AppendLine();
        AppendOutput(section, result);
        section.AppendLine();
    }

    private static void AppendOutput(StringBuilder section, HostProcessResult result)
    {
        if (!result.IsSuccess)
        {
            section.AppendLine($"_Unavailable: {SafeError(result)}_");
            return;
        }

        section.AppendLine("```text");
        section.AppendLine(result.StdOut.TrimEnd());
        section.AppendLine("```");
    }

    private static string SafeError(HostProcessResult result)
        => result.ExitCode == -1 ? "tool not installed" : $"command exited with code {result.ExitCode}";

    private static void ApplyRetention(string directory, RetentionPolicy retention)
    {
        var files = Directory.GetFiles(directory, "inventory_*.md")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        var cutoff = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);

        foreach (var file in files.Skip(retention.MaxCount).Where(file => file.LastWriteTimeUtc < cutoff))
        {
            File.Delete(file.FullName);
            var checksum = BackupIntegrity.GetChecksumPath(file.FullName);
            if (File.Exists(checksum))
                File.Delete(checksum);
        }
    }

    private static string GetVersion()
        => typeof(InventoryService).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(InventoryService).Assembly.GetName().Version?.ToString()
           ?? "unknown";
}

public sealed record InventoryResult(bool Success, string? FilePath, string? ErrorMessage);
